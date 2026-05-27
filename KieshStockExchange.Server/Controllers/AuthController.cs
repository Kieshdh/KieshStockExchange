using KieshStockExchange.Helpers;
using KieshStockExchange.Server.Services.UserServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace KieshStockExchange.Server.Controllers;

/// <summary>
/// Step 3a — username/password → JWT exchange. Phase 6 will add refresh
/// tokens + password reset; for now the login endpoint is the only one.
/// </summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly IDataBaseService _db;
    private readonly JwtTokenService _tokens;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IDataBaseService db, JwtTokenService tokens, ILogger<AuthController> logger)
    {
        _db = db;
        _tokens = tokens;
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
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, DateTime ExpiresUtc, int UserId, string Username, bool IsAdmin);
