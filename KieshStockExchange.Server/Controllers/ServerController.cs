using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

// Graceful shutdown over HTTP. Calls IHostApplicationLifetime.StopApplication()
// which runs the same path as Ctrl+C — IHostedService.StopAsync on every
// hosted service (including BotLoopHostedService → bot loop drains, ringbuffer
// flushes, etc.) — but is invokable from anywhere and can't be missed by VS's
// "Stop Debugging" eating the signal. See stop-server.ps1 in the repo root.
//
// Admin-gated: an anonymous shutdown endpoint would let anyone stop the public server.
// Host operators use `docker stop`; remote admins authenticate.
[ApiController]
[Route("api/server")]
[Authorize(Roles = "admin")]
public sealed class ServerController : ControllerBase
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ServerController> _logger;

    public ServerController(IHostApplicationLifetime lifetime, ILogger<ServerController> logger)
    {
        _lifetime = lifetime;
        _logger   = logger;
    }

    [HttpPost("shutdown")]
    public IActionResult Shutdown()
    {
        _logger.LogInformation("Shutdown requested via /api/server/shutdown.");
        // Return immediately; the host runs StopAsync on hosted services after
        // the response is flushed. Caller does not need to wait.
        _lifetime.StopApplication();
        return Accepted(new { status = "shutting down" });
    }
}
