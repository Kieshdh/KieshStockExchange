using KieshStockExchange.Services.Telemetry;

namespace KieshStockExchange.Server.Services.Telemetry;

/// <summary>
/// In-process fan-out for <see cref="TelemetryEvent"/>. The Serilog sink
/// publishes; the SignalR broadcaster and the SSE endpoint each subscribe.
/// Decoupled so neither delivery path can stall a log write.
/// </summary>
public sealed class TelemetryBus
{
    // Rolling history so a freshly-opened viewer can backfill what happened before it
    // connected. Capped to bound memory; oldest events fall off.
    private const int HistoryCapacity = 500;

    private readonly object _lock = new();
    private readonly List<Action<TelemetryEvent>> _subscribers = new();
    private readonly Queue<TelemetryEvent> _history = new();

    public void Publish(TelemetryEvent evt)
    {
        // Buffer for replay + snapshot subscribers under one lock so a connecting
        // subscriber sees a consistent history (no gap, no duplicate); invoke outside the
        // lock so a slow/throwing subscriber can't hold it or kill the publish path.
        Action<TelemetryEvent>[] snapshot;
        lock (_lock)
        {
            _history.Enqueue(evt);
            while (_history.Count > HistoryCapacity) _history.Dequeue();
            snapshot = _subscribers.ToArray();
        }
        foreach (var sub in snapshot)
        {
            try { sub(evt); } catch { /* a broken subscriber must not break the bus */ }
        }
    }

    public IDisposable Subscribe(Action<TelemetryEvent> handler)
        => Subscribe(handler, out _);

    /// <summary>
    /// Subscribe and atomically receive the buffered history (oldest-first). Snapshot and
    /// registration happen under one lock, so every event reaches the subscriber exactly
    /// once — in <paramref name="history"/> or live, never both, never neither.
    /// </summary>
    public IDisposable Subscribe(Action<TelemetryEvent> handler, out TelemetryEvent[] history)
    {
        lock (_lock)
        {
            history = _history.ToArray();
            _subscribers.Add(handler);
        }
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
