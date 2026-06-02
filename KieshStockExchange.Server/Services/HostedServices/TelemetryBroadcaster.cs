using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Server.Services.Telemetry;
using KieshStockExchange.Services.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace KieshStockExchange.Server.Services.HostedServices;

// Bridges TelemetryBus events onto the SignalR "telemetry" group so the
// BotDashboard live panel receives the heartbeats the operator used to read in
// the dev console. Mirrors MarketHubBroadcaster's subscribe-at-boot lifecycle.
public sealed class TelemetryBroadcaster : IHostedService
{
    private readonly IHubContext<MarketHub> _hub;
    private readonly TelemetryBus _bus;
    private readonly ILogger<TelemetryBroadcaster> _logger;
    private IDisposable? _subscription;

    public TelemetryBroadcaster(IHubContext<MarketHub> hub, TelemetryBus bus, ILogger<TelemetryBroadcaster> logger)
    {
        _hub = hub;
        _bus = bus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(OnTelemetry);
        _logger.LogInformation("TelemetryBroadcaster subscribed to telemetry bus.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void OnTelemetry(TelemetryEvent evt)
    {
        // Fire-and-forget so the bus publish (running on the log-write path)
        // never blocks on a slow client.
        _ = _hub.Clients.Group(MarketHub.GroupNameTelemetry).SendAsync("OnTelemetryEvent", evt)
            .ContinueWith(t =>
            {
                if (t.IsFaulted) _logger.LogWarning(t.Exception, "Telemetry push failed");
            }, TaskScheduler.Default);
    }
}
