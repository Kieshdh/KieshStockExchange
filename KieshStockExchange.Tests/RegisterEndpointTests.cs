using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Controllers;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// The human-trading doorway (docs/HUMAN_TRADING_DOORWAY_PLAN.md): self-service
/// <c>POST /api/auth/register</c> must create a NON-admin trader, provision seed cash so
/// they can trade immediately, and hand back a JWT (auto-login). These lock the security
/// invariant (never admin via self-service) and the seed-fund provisioning.
/// </summary>
public class RegisterEndpointTests
{
    private const string ValidPassword = "hunter2pw";        // >= 8 chars
    private static DateTime AdultBirthDate => DateTime.UtcNow.AddYears(-25);

    private static AuthController Build(Mock<IDataBaseService> db, decimal? seedUsd = null)
    {
        var jwt = new JwtTokenService(new JwtSettings
        {
            SigningKey = "unit-test-signing-key-please-32-bytes-min!!",
            TokenLifetimeHours = 1,
        });
        var settings = new Dictionary<string, string?>();
        if (seedUsd is not null)
            settings["Users:SeedBalanceUsd"] = seedUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new AuthController(db.Object, jwt, config, NullLogger<AuthController>.Instance);
    }

    private static Mock<IDataBaseService> DbWithNoExistingUser(out System.Collections.Generic.List<User> created,
        out System.Collections.Generic.List<Fund> funds)
    {
        var db = new Mock<IDataBaseService>();
        db.Setup(d => d.GetUserByUsername(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((User?)null);
        var createdLocal = new System.Collections.Generic.List<User>();
        var fundsLocal = new System.Collections.Generic.List<Fund>();
        db.Setup(d => d.CreateUser(It.IsAny<User>(), It.IsAny<CancellationToken>()))
          .Callback<User, CancellationToken>((u, _) => { u.UserId = 5001; createdLocal.Add(u); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.CreateFund(It.IsAny<Fund>(), It.IsAny<CancellationToken>()))
          .Callback<Fund, CancellationToken>((f, _) => fundsLocal.Add(f))
          .Returns(Task.CompletedTask);
        created = createdLocal;
        funds = fundsLocal;
        return db;
    }

    [Fact]
    public async Task Register_ValidRequest_CreatesNonAdminUser_SeedsCash_ReturnsToken()
    {
        var db = DbWithNoExistingUser(out var created, out var funds);
        var controller = Build(db);   // default seed = 100_000 USD
        var req = new RegisterRequest("trader01", ValidPassword, "trader01@example.com", "Trader One", AdultBirthDate);

        var result = await controller.Register(req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<LoginResponse>(ok.Value);
        Assert.False(body.IsAdmin);                         // never admin via self-service
        Assert.Equal(5001, body.UserId);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));

        var user = Assert.Single(created);
        Assert.False(user.IsAdmin);                          // stored non-admin
        var fund = Assert.Single(funds);
        Assert.Equal(100_000m, fund.TotalBalance);
        Assert.Equal(CurrencyType.USD, fund.CurrencyType);
        Assert.Equal(5001, fund.UserId);
    }

    [Fact]
    public async Task Register_ClientRequestedAdmin_IsIgnored()
    {
        // RegisterRequest has no admin field by design; even so, confirm the created user is non-admin.
        var db = DbWithNoExistingUser(out var created, out _);
        var controller = Build(db);
        var req = new RegisterRequest("trader02", ValidPassword, "t2@example.com", "Trader Two", AdultBirthDate);

        await controller.Register(req, CancellationToken.None);

        Assert.False(Assert.Single(created).IsAdmin);
    }

    [Fact]
    public async Task Register_CustomSeedBalance_IsHonored()
    {
        var db = DbWithNoExistingUser(out _, out var funds);
        var controller = Build(db, seedUsd: 25_000m);
        var req = new RegisterRequest("trader03", ValidPassword, "t3@example.com", "Trader Three", AdultBirthDate);

        await controller.Register(req, CancellationToken.None);

        Assert.Equal(25_000m, Assert.Single(funds).TotalBalance);
    }

    [Fact]
    public async Task Register_ZeroSeedBalance_ProvisionsNoFund()
    {
        var db = DbWithNoExistingUser(out _, out var funds);
        var controller = Build(db, seedUsd: 0m);
        var req = new RegisterRequest("trader04", ValidPassword, "t4@example.com", "Trader Four", AdultBirthDate);

        var result = await controller.Register(req, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(funds);
        db.Verify(d => d.CreateFund(It.IsAny<Fund>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Register_DuplicateUsername_ReturnsConflict_NoCreate()
    {
        var db = new Mock<IDataBaseService>();
        db.Setup(d => d.GetUserByUsername(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new User { Username = "trader05", Email = "x@example.com", FullName = "Existing",
              PasswordHash = SecurityHelper.HashPassword(ValidPassword), BirthDate = AdultBirthDate, UserId = 42 });
        var controller = Build(db);
        var req = new RegisterRequest("trader05", ValidPassword, "t5@example.com", "Trader Five", AdultBirthDate);

        var result = await controller.Register(req, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        db.Verify(d => d.CreateUser(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Register_ShortPassword_ReturnsBadRequest_NoCreate()
    {
        var db = DbWithNoExistingUser(out _, out _);
        var controller = Build(db);
        var req = new RegisterRequest("trader06", "short", "t6@example.com", "Trader Six", AdultBirthDate);

        var result = await controller.Register(req, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        db.Verify(d => d.CreateUser(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Register_Underage_ReturnsBadRequest_NoCreate()
    {
        var db = DbWithNoExistingUser(out _, out _);
        var controller = Build(db);
        var req = new RegisterRequest("trader07", ValidPassword, "t7@example.com", "Trader Seven",
            DateTime.UtcNow.AddYears(-15));

        var result = await controller.Register(req, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        db.Verify(d => d.CreateUser(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
