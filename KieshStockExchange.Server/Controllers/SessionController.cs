using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Phase 3 follow-up: server-side visibility into client logins / logouts.
// Auth still happens client-side (Phase 5's JWT moves it server-side); for now
// the client posts a notice here on successful login + voluntary logout so the
// server log shows who's connected. No security claim — anyone can hit these
// endpoints — but they're useful for "what users have been on the box."
[ApiController]
[Route("api/session")]
public sealed class SessionController : ControllerBase
{
    private readonly ILogger<SessionController> _logger;
    public SessionController(ILogger<SessionController> logger) => _logger = logger;

    [HttpPost("login")]
    public IActionResult Login([FromBody] SessionEvent ev)
    {
        if (ev is null) return BadRequest();
        _logger.LogInformation("Session: user #{UserId} {Username} logged in (client {Client}).",
            ev.UserId, ev.Username ?? "?", ev.ClientLabel ?? "unknown");
        return NoContent();
    }

    [HttpPost("logout")]
    public IActionResult Logout([FromBody] SessionEvent ev)
    {
        if (ev is null) return BadRequest();
        _logger.LogInformation("Session: user #{UserId} {Username} logged out.",
            ev.UserId, ev.Username ?? "?");
        return NoContent();
    }
}

public sealed record SessionEvent(int UserId, string? Username, string? ClientLabel);
