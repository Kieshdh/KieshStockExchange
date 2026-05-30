using KieshStockExchange.Server.Services.RetentionServices;
using Microsoft.Extensions.Hosting;

namespace KieshStockExchange.Server.Services.HostedServices;

/// <summary>
/// Wave 8 §3 — drives the recurring history prune. Gated on <c>Retention:Enabled</c>
/// (mirrors <see cref="BotLoopHostedService"/>'s config gate); when enabled it ticks
/// a <see cref="PeriodicTimer"/> every <c>Retention:IntervalMinutes</c> and runs one
/// live cycle per tick. A failing tick is logged and the loop continues — one bad
/// cycle must not kill retention. Flipping <c>Retention:Enabled=false</c> + restart
/// is the operational off switch; the on-demand controller endpoints work regardless.
/// </summary>
public sealed class RetentionHostedService : BackgroundService
{
    private readonly IRetentionService _retention;
    private readonly IConfiguration _config;
    private readonly ILogger<RetentionHostedService> _logger;

    public RetentionHostedService(
        IRetentionService retention, IConfiguration config, ILogger<RetentionHostedService> logger)
    {
        _retention = retention;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Retention:Enabled", false))
        {
            _logger.LogInformation("RetentionHostedService: Retention:Enabled=false, retention loop dormant.");
            return;
        }

        var intervalMinutes = Math.Max(1, _config.GetValue("Retention:IntervalMinutes", 30));
        _logger.LogInformation("RetentionHostedService: starting; interval {Minutes} min.", intervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _retention.RunOnceAsync(dryRun: false, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RetentionHostedService: prune cycle failed; will retry next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }
}
