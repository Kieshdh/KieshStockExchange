using KieshStockExchange.Server.Services.SeedServices.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

/// <summary>
/// 7b — admin-gated database seeding from the AIUserData workbook. The JWT role
/// claim is issued lowercase ("admin"), so the gate matches that exactly.
/// </summary>
[ApiController]
[Route("api/admin/seed/excel")]
[Authorize(Roles = "admin")]
public sealed class SeedController : ControllerBase
{
    private readonly IExcelSeedService _seed;
    private readonly ILogger<SeedController> _logger;

    public SeedController(IExcelSeedService seed, ILogger<SeedController> logger)
    {
        _seed = seed;
        _logger = logger;
    }

    /// <summary>Runs all five seed steps from an uploaded workbook.</summary>
    [HttpPost("full")]
    public async Task<IActionResult> Full(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("A workbook file is required.");
        await using var stream = file.OpenReadStream();
        await _seed.SeedAllAsync(stream, ct);
        _logger.LogInformation("Excel full seed completed from upload {FileName} ({Bytes} bytes).",
            file.FileName, file.Length);
        return Ok(new { seeded = "full" });
    }

    /// <summary>Runs a single seed step (stocks, listings, users, ai-profiles, holdings).</summary>
    [HttpPost("{kind}")]
    public async Task<IActionResult> Kind(string kind, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("A workbook file is required.");
        await using var stream = file.OpenReadStream();
        try
        {
            await _seed.SeedKindAsync(kind, stream, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        return Ok(new { seeded = kind });
    }

    /// <summary>Runs all five steps from the server's embedded workbook.</summary>
    [HttpPost("from-embedded")]
    public async Task<IActionResult> FromEmbedded(CancellationToken ct)
    {
        var ran = await _seed.SeedAllFromEmbeddedAsync(ct);
        return ran ? Ok(new { seeded = "full", source = "embedded" })
                   : NotFound("Embedded workbook not found.");
    }
}
