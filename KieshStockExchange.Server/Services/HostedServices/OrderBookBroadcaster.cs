using KieshStockExchange.Helpers;
using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Services.MarketEngineServices;
using Microsoft.AspNetCore.SignalR;

namespace KieshStockExchange.Server.Services.HostedServices;

// Step 0g layer A → SignalR — subscribes OrderBook.Changed on every live
// book, throttles pushes to max 1 per 100ms per key, and broadcasts the
// resulting OrderBookSnapshot onto quotes:{stockId}:{currency} (same group
// the chart already joins for quote ticks). Hash-skip via BookVersion so a
// flush with no real change is a no-op.
public sealed class OrderBookBroadcaster : IHostedService, IAsyncDisposable
{
    private const int ThrottleMs = 100;
    private const int TickerMs = 50;

    private readonly IOrderBookEngine _engine;
    private readonly IHubContext<MarketHub> _hub;
    private readonly ILogger<OrderBookBroadcaster> _logger;

    private readonly Dictionary<(int, CurrencyType), KeyState> _state = new();
    private readonly object _stateLock = new();
    private readonly HashSet<OrderBook> _subscribed = new();

    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _loop;

    public OrderBookBroadcaster(IOrderBookEngine engine, IHubContext<MarketHub> hub,
        ILogger<OrderBookBroadcaster> logger)
    {
        _engine = engine;
        _hub = hub;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_shutdownCts.Token));
        _logger.LogInformation("OrderBookBroadcaster started (throttle {ThrottleMs}ms, ticker {TickerMs}ms).",
            ThrottleMs, TickerMs);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        return _loop ?? Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickerMs));
        while (!ct.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            SyncSubscriptions();
            FlushDirty();
        }
    }

    /// <summary>
    /// Lazily attach Changed handlers to any newly-created books. The bot
    /// loop and chart subscriptions create books over time; we catch them on
    /// the next ticker tick. Books are never destroyed at runtime.
    /// </summary>
    private void SyncSubscriptions()
    {
        foreach (var book in _engine.EnumerateBooks())
        {
            if (_subscribed.Add(book))
            {
                book.Changed += OnBookChanged;
            }
        }
    }

    private void OnBookChanged(object? sender, EventArgs _)
    {
        if (sender is not OrderBook book) return;
        var key = (book.StockId, book.Currency);
        lock (_stateLock)
        {
            if (!_state.TryGetValue(key, out var s))
            {
                s = new KeyState();
                _state[key] = s;
            }
            s.Dirty = true;
        }
    }

    private void FlushDirty()
    {
        var now = DateTime.UtcNow;
        List<(int stockId, CurrencyType currency, OrderBookSnapshot snapshot)>? toSend = null;

        lock (_stateLock)
        {
            foreach (var (key, state) in _state)
            {
                if (!state.Dirty) continue;
                if ((now - state.LastPushedUtc).TotalMilliseconds < ThrottleMs) continue;

                // Take a snapshot under the book's gate (ToDepthSnapshot is
                // sync). BookVersion gate-skips a no-op flush.
                var snap = SnapshotFor(key);
                if (snap is null) continue;
                if (snap.BookVersion == state.LastPushedVersion)
                {
                    state.Dirty = false;
                    continue;
                }

                state.Dirty = false;
                state.LastPushedUtc = now;
                state.LastPushedVersion = snap.BookVersion;
                (toSend ??= new()).Add((key.Item1, key.Item2, snap));
            }
        }

        if (toSend is null) return;
        foreach (var (stockId, currency, snap) in toSend)
        {
            var group = MarketHub.GroupNameQuotes(stockId, currency);
            _ = _hub.Clients.Group(group).SendAsync("OrderBookSnapshot", snap)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _logger.LogWarning(t.Exception, "OrderBookSnapshot push failed for {Group}", group);
                }, TaskScheduler.Default);
        }
    }

    private OrderBookSnapshot? SnapshotFor((int stockId, CurrencyType currency) key)
    {
        try
        {
            // GetSnapshotAsync is non-blocking once the book is loaded — bots
            // hit every book at boot via SubscribeAllAsync so cold-load is
            // not the common case here. Sync the result since we're inside
            // the ticker.
            return _engine.GetSnapshotAsync(key.stockId, key.currency, _shutdownCts.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetSnapshotAsync failed for {Stock}/{Currency}", key.stockId, key.currency);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        foreach (var book in _subscribed) book.Changed -= OnBookChanged;
        _shutdownCts.Dispose();
    }

    private sealed class KeyState
    {
        public bool Dirty;
        public DateTime LastPushedUtc;
        public long LastPushedVersion;
    }
}
