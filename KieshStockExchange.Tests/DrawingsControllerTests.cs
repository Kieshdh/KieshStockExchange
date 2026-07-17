using System.Security.Claims;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Controllers;
using KieshStockExchange.Services.DataServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

// UP-STORE — ownership is derived SOLELY from the JWT claim; with no user claim every action must
// Forbid (a caller can never address another user's rows). The store is never reached on that path.
public sealed class DrawingsControllerTests
{
    private static DrawingsController MakeUnauthed()
    {
        var store = new UserDrawingStore(Mock.Of<IUserDrawingQueries>(),
            new ConfigurationBuilder().Build(), NullLogger<UserDrawingStore>.Instance);
        return new DrawingsController(store)
        {
            // No auth claims → User.GetUserId() is null.
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) },
            },
        };
    }

    [Fact]
    public async Task Get_WithoutUserClaim_Forbids()
    {
        var result = await MakeUnauthed().Get(1, "USD", CancellationToken.None);
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public void Save_WithoutUserClaim_Forbids()
    {
        var result = MakeUnauthed().Save(1, "USD", new DrawingPayload("{\"v\":1}"));
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public void Delete_WithoutUserClaim_Forbids()
    {
        var result = MakeUnauthed().Delete(1, "USD");
        Assert.IsType<ForbidResult>(result);
    }
}
