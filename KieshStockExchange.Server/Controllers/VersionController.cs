using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KieshStockExchange.Server.Controllers;

/// <summary>
/// 7a-1 — anonymous build/version probe. Clients and ops tooling hit this to
/// confirm which build is live and how long it has been up.
/// </summary>
[ApiController]
[Route("api/version")]
[AllowAnonymous]
public sealed class VersionController : ControllerBase
{
    // Process start time is a stable per-instance value with no DI plumbing.
    private static readonly DateTime StartedAtUtc =
        Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private readonly IHostEnvironment _env;

    public VersionController(IHostEnvironment env) => _env = env;

    [HttpGet]
    public IActionResult Get()
    {
        var asm = Assembly.GetEntryAssembly();
        var version = asm?.GetName().Version?.ToString() ?? "unknown";
        var build = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? version;
        return Ok(new
        {
            version,
            build,
            environment = _env.EnvironmentName,
            startedAtUtc = StartedAtUtc,
        });
    }
}
