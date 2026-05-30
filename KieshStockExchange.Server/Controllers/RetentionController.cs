using KieshStockExchange.Server.Services.RetentionServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

/// <summary>
/// Wave 8 §3 — admin-gated retention controls. The JWT role claim is issued
/// lowercase ("admin"), so the gate matches that exactly (same as SeedController).
/// <list type="bullet">
/// <item><c>GET preview</c> — dry run: returns the counts that would be deleted,
/// deletes nothing. The calibration / safety-inspection surface.</item>
/// <item><c>POST run</c> — runs one live prune cycle on demand. Primary smoke test.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/admin/retention")]
[Authorize(Roles = "admin")]
public sealed class RetentionController : ControllerBase
{
    private readonly IRetentionService _retention;
    private readonly ILogger<RetentionController> _logger;

    public RetentionController(IRetentionService retention, ILogger<RetentionController> logger)
    {
        _retention = retention;
        _logger = logger;
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var report = await _retention.RunOnceAsync(dryRun: true, ct);
        return Ok(report);
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct)
    {
        _logger.LogInformation("RetentionController: on-demand prune requested.");
        var report = await _retention.RunOnceAsync(dryRun: false, ct);
        return Ok(report);
    }
}
