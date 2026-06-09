using KieshStockExchange.Services.Telemetry;

namespace KieshStockExchange.Server.Services.Telemetry;

/// <summary>
/// In-process fan-out for <see cref="TelemetryEvent"/>. The Serilog sink
/// publishes; the SignalR broadcaster and the SSE endpoint each subscribe.
/// Decoupled so neither delivery path can stall a log write.
/// </summary>
public sealed class TelemetryBus
{
    // Per-CATEGORY rolling history so a freshly-opened viewer can backfill a full day
    // and downsample it to any timeframe (1m/5m/15m/1h) client-side. Each category keeps
    // the last RetentionMinutes of events; a hard per-category count cap bounds memory if
    // a category ever bursts faster than once a minute.
    private const int RetentionMinutes = 1440;          // 24h
    private const int MaxPerCategory   = 5000;          // safety cap (well above 1440 1/min samples)

    private readonly object _lock = new();
    private readonly List<Action<TelemetryEvent>> _subscribers = new();
    private readonly Dictionary<string, Queue<TelemetryEvent>> _historyByCategory = new();

    public void Publish(TelemetryEvent evt)
    {
        // Buffer for replay + snapshot subscribers under one lock so a connecting
        // subscriber sees a consistent history (no gap, no duplicate); invoke outside the
        // lock so a slow/throwing subscriber can't hold it or kill the publish path.
        Action<TelemetryEvent>[] snapshot;
        lock (_lock)
        {
            if (!_historyByCategory.TryGetValue(evt.Category, out var q))
                _historyByCategory[evt.Category] = q = new Queue<TelemetryEvent>();
            q.Enqueue(evt);
            var cutoff = evt.Timestamp - TimeSpan.FromMinutes(RetentionMinutes);
            while (q.Count > 0 && (q.Count > MaxPerCategory || q.Peek().Timestamp < cutoff))
                q.Dequeue();
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
            // Flatten every category's buffer into one oldest-first backfill so the viewer
            // can render any category at any timeframe without a per-category round-trip.
            history = _historyByCategory.Values
                .SelectMany(q => q)
                .OrderBy(e => e.Timestamp)
                .ToArray();
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
