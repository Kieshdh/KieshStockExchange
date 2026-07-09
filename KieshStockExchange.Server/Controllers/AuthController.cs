using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KieshStockExchange.Server.Controllers;

/// <summary>
/// Step 3a — username/password → JWT exchange, plus self-service registration
/// (the human-trading doorway). Phase 6 will add refresh tokens + password reset.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly IDataBaseService _db;
    private readonly JwtTokenService _tokens;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IDataBaseService db, JwtTokenService tokens,
        IConfiguration configuration, ILogger<AuthController> logger)
    {
        _db = db;
        _tokens = tokens;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest("username and password are required.");

        var user = await _db.GetUserByUsername(req.Username, ct).ConfigureAwait(false);
        if (user is null || !SecurityHelper.VerifyPassword(req.Password, user.PasswordHash))
        {
            // Same 401 + opaque message either way — prevents username enumeration.
            return Unauthorized(new { error = "invalid_credentials" });
        }

        var issued = _tokens.IssueFor(user);
        _logger.LogInformation("Issued JWT for user #{UserId} {Username} (admin={Admin}); expires {Expires:o}",
            issued.UserId, issued.Username, issued.IsAdmin, issued.ExpiresUtc);
        return Ok(new LoginResponse(issued.Token, issued.ExpiresUtc, issued.UserId, issued.Username, issued.IsAdmin));
    }

    // Self-service registration: creates a NON-admin human trader, provisions seed cash
    // so they can immediately trade against the live market, and returns a JWT (auto-login).
    // Server-authoritative on IsAdmin — a registrant can never grant themselves admin.
    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (req is null) return BadRequest(new { error = "missing_body" });
        if (!SecurityHelper.IsValidPassword(req.Password))
            return BadRequest(new { error = "invalid_password", detail = "Password must be at least 8 characters." });

        var user = new User
        {
            Username = req.Username ?? string.Empty,
            FullName = req.FullName ?? string.Empty,
            Email = req.Email ?? string.Empty,
            PasswordHash = SecurityHelper.HashPassword(req.Password),
            BirthDate = req.BirthDate,
            IsAdmin = false, // never admin via self-service, whatever the client sends
        };
        if (!user.IsValid())
            return BadRequest(new { error = "invalid_user",
                detail = "Username must be 5-20 alphanumeric; email valid; full name set; and you must be 18+." });

        // Username is unique + indexed — cheap pre-check for a clean 409 (DB constraint is the backstop).
        if (await _db.GetUserByUsername(user.Username, ct).ConfigureAwait(false) is not null)
            return Conflict(new { error = "username_taken" });

        try
        {
            await _db.CreateUser(user, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registration failed for username {Username}.", user.Username);
            return Conflict(new { error = "registration_failed" });
        }

        // Seed cash so the new trader can place orders against the live bot market immediately.
        var seedUsd = _configuration.GetValue("Users:SeedBalanceUsd", 100_000m);
        if (seedUsd > 0m)
        {
            var fund = new Fund { UserId = user.UserId, TotalBalance = seedUsd, CurrencyType = CurrencyType.USD };
            if (fund.IsValid())
                await _db.CreateFund(fund, ct).ConfigureAwait(false);
            else
                _logger.LogWarning("Seed fund invalid for new user #{UserId} ({Seed} USD).", user.UserId, seedUsd);
        }

        var issued = _tokens.IssueFor(user);
        _logger.LogInformation("Registered user #{UserId} {Username}; seeded {Seed} USD.",
            issued.UserId, issued.Username, seedUsd);
        return Ok(new LoginResponse(issued.Token, issued.ExpiresUtc, issued.UserId, issued.Username, issued.IsAdmin));
    }
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTime ExpiresUtc, int UserId, string Username, bool IsAdmin);
public sealed record RegisterRequest(string Username, string Password, string Email, string FullName, DateTime BirthDate);
