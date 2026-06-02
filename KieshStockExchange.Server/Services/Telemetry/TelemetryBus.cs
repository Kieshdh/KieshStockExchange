using KieshStockExchange.Services.Telemetry;

namespace KieshStockExchange.Server.Services.Telemetry;

/// <summary>
/// In-process fan-out for <see cref="TelemetryEvent"/>. The Serilog sink
/// publishes; the SignalR broadcaster and the SSE endpoint each subscribe.
/// Decoupled so neither delivery path can stall a log write.
/// </summary>
public sealed class TelemetryBus
{
    private readonly object _lock = new();
    private readonly List<Action<TelemetryEvent>> _subscribers = new();

    public void Publish(TelemetryEvent evt)
    {
        // Snapshot under lock, invoke outside it — a slow/throwing subscriber
        // must not hold the lock or kill the publish path.
        Action<TelemetryEvent>[] snapshot;
        lock (_lock) snapshot = _subscribers.ToArray();
        foreach (var sub in snapshot)
        {
            try { sub(evt); } catch { /* a broken subscriber must not break the bus */ }
        }
    }

    public IDisposable Subscribe(Action<TelemetryEvent> handler)
    {
        lock (_lock) _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<TelemetryEvent> handler)
    {
        lock (_lock) _subscribers.Remove(handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly TelemetryBus _bus;
        private readonly Action<TelemetryEvent> _handler;
        private bool _disposed;

        public Subscription(TelemetryBus bus, Action<TelemetryEvent> handler)
        {
            _bus = bus;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bus.Unsubscribe(_handler);
        }
    }
}
