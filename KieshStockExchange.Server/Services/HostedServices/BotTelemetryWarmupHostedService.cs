using KieshStockExchange.Services.BackgroundServices.Helpers;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Server.Services.HostedServices;

/// <summary>
/// Pre-populates the BotDashboard's two slow telemetry queries on a
/// background thread shortly after startup so the first client poll hits
/// a warm cache instead of waiting ~10s on a cold DB scan.
/// </summary>
public sealed class BotTelemetryWarmupHostedService : BackgroundService
{
    private readonly BotTelemetryCache _cache;
    private readonly ILogger<BotTelemetryWarmupHostedService> _logger;

    public BotTelemetryWarmupHostedService(BotTelemetryCache cache, ILogger<BotTelemetryWarmupHostedService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay so the bot loop has registered AI user IDs before the
        // first scan runs; otherwise the cache primes with zero participants.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("BotTelemetryCache warm-up starting.");
        await _cache.WarmAsync(stoppingToken).ConfigureAwait(false);
    }
}
