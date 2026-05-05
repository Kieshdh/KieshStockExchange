using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Engine-facing tick pipeline. Buffers transactions in an unbounded
/// <see cref="Channel{T}"/> so engine threads return immediately, then drains them on
/// a single reader that feeds candle aggregators, applies ticks to LiveQuotes (UI- or
/// bot-bound), and marks the registry dirty for the next QuoteUpdated drain. Also
/// hosts <see cref="BuildFromHistoryAsync"/> for first-subscribe seeding from
/// persisted ticks.
/// </summary>
internal sealed class TickPipeline
{
    private readonly Channel<IReadOnlyList<Transaction>> _channel =
        Channel.CreateUnbounded<IReadOnlyList<Transaction>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _readerCts = new();

    private readonly QuoteRegistry _registry;
    private readonly SubscriptionTracker _subs;
    private readonly ICandleService _candle;
    private readonly IDispatcher _dispatcher;
    private readonly IMarketLookupService _lookup;
    private readonly ILogger<TickPipeline> _logger;

    private Task? _readerTask;

    public TickPipeline(
        QuoteRegistry registry,
        SubscriptionTracker subs,
        ICandleService candle,
        IDispatcher dispatcher,
        IMarketLookupService lookup,
        ILogger<TickPipeline> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _subs = subs ?? throw new ArgumentNullException(nameof(subs));
        _candle = candle ?? throw new ArgumentNullException(nameof(candle));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start()
    {
        _readerTask = Task.Run(() => ReadLoopAsync(_readerCts.Token));
    }

    public async Task StopAsync()
    {
        try { _channel.Writer.TryComplete(); } catch { /* ignore */ }
        try { _readerCts.Cancel(); } catch { /* ignore */ }
        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _readerCts.Dispose();
    }

    public Task OnTick(Transaction tick, CancellationToken ct = default)
    {
        if (!tick.IsValid())
        {
            _logger.LogWarning("Invalid tick received: {@Tick}", tick);
            return Task.CompletedTask;
        }

        // Route through the channel â€” the reader handles candle feed and UI dispatch.
        // Engine threads return immediately.
        _channel.Writer.TryWrite(new[] { tick });
        return Task.CompletedTask;
    }

    public Task OnTicksAsync(IReadOnlyList<Transaction> ticks, CancellationToken ct = default)
    {
        if (ticks is null || ticks.Count == 0) return Task.CompletedTask;

        // Validate without allocating in the common case (all valid).
        bool allValid = true;
        for (int i = 0; i < ticks.Count; i++)
        {
            if (!ticks[i].IsValid())
            {
                allValid = false;
                _logger.LogWarning("Invalid tick received in batch: {@Tick}", ticks[i]);
                break;
            }
        }

        if (allValid)
        {
            _channel.Writer.TryWrite(ticks);
            return Task.CompletedTask;
        }

        // Filter invalids on the rare invalid-tick path.
        var filtered = new List<Transaction>(ticks.Count);
        for (int i = 0; i < ticks.Count; i++)
        {
            var t = ticks[i];
            if (t.IsValid()) filtered.Add(t);
            else _logger.LogWarning("Invalid tick received in batch: {@Tick}", t);
        }
        if (filtered.Count > 0)
            _channel.Writer.TryWrite(filtered);
        return Task.CompletedTask;
    }

    public async Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var quote = await _registry.GetOrAddAsync(stockId, currency, ct).ConfigureAwait(false);

        // Already populated â€” quote.LastUpdated is set by ApplyTick / ApplySnapshot.
        if (quote.LastUpdated > DateTime.MinValue)
            return;

        var history = await _lookup.LoadHistoricalTicksAsync(stockId, currency, ct).ConfigureAwait(false);
        if (history.Count == 0)
        {
            var (price, time) = await _lookup.GetFallbackPriceAndTimeAsync(stockId, currency, ct).ConfigureAwait(false);
            _registry.ApplyOnPreferredThread((stockId, currency), () =>
                quote.ApplySnapshot(price, 0, price, price, price, time));
            _registry.MarkDirty((stockId, currency));
            return;
        }

        // Compute OHLC + volume in one pass.
        decimal open = history[0].Price;
        decimal high = open;
        decimal low = open;
        long volume = 0;
        for (int i = 0; i < history.Count; i++)
        {
            var p = history[i].Price;
            if (p > high) high = p;
            if (p < low) low = p;
            volume += history[i].Quantity;
        }

        var lastUtc = TimeHelper.EnsureUtc(history[^1].Timestamp);
        var last = history[^1].Price;
        var vol = volume > int.MaxValue ? int.MaxValue : (int)volume;

        _registry.ApplyOnPreferredThread((stockId, currency), () =>
            quote.ApplySnapshot(last, vol, open, high, low, lastUtc));
        _registry.MarkDirty((stockId, currency));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        // ReadAllAsync without a cancellation token exits cleanly when the writer is
        // completed (which StopAsync does first). Avoiding the token here keeps the
        // debugger's first-chance OCE window quiet on shutdown without delaying exit â€”
        // the channel typically holds a tiny backlog at the 250ms drain cadence.
        try
        {
            await foreach (var batch in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                try { await ProcessBatchAsync(batch, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* shutdown */ break; }
                catch (Exception ex) { _logger.LogError(ex, "Error processing tick batch."); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogError(ex, "Tick channel reader terminated unexpectedly."); }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<Transaction> ticks, CancellationToken ct)
    {
        // Single-tick fast path. Engine OnTick is the dominant case in the steady
        // state â€” skipping the grouping Dictionary and per-book List avoids two
        // allocations per tick.
        if (ticks.Count == 1)
        {
            await ProcessOneAsync(ticks[0], ct).ConfigureAwait(false);
            return;
        }

        // Multi-tick path: group by (stockId, currency) so we coalesce UI dispatches
        // per book.
        var byBook = new Dictionary<(int, CurrencyType), List<Transaction>>(ticks.Count);
        for (int i = 0; i < ticks.Count; i++)
        {
            var t = ticks[i];
            var key = (t.StockId, t.CurrencyType);
            if (!byBook.TryGetValue(key, out var list))
            {
                list = new List<Transaction>(4);
                byBook[key] = list;
            }
            list.Add(t);
        }

        foreach (var kv in byBook)
        {
            var (stockId, currency) = kv.Key;
            var list = kv.Value;

            // Feed candle aggregators (synchronous; persistence happens in
            // CandleService.FlushLoopAsync).
            for (int i = 0; i < list.Count; i++)
                _candle.OnTransactionTick(list[i]);

            // Skip live-quote work entirely if nobody is subscribed to this book.
            if (!_subs.HasAnySubscribers(kv.Key)) continue;

            var quote = await _registry.GetOrAddAsync(stockId, currency, ct).ConfigureAwait(false);
            var hasUi = _subs.HasUiSubscribers(kv.Key);

            if (hasUi)
            {
                // Single UI dispatch applies all ticks for this book.
                var snapshot = list;
                _dispatcher.Dispatch(() =>
                {
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        var t = snapshot[i];
                        quote.ApplyTick(t.Price, t.Quantity, TimeHelper.EnsureUtc(t.Timestamp));
                    }
                });
            }
            else
            {
                // Bot-only book: apply ticks directly on the reader thread.
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    quote.ApplyTick(t.Price, t.Quantity, TimeHelper.EnsureUtc(t.Timestamp));
                }
            }

            _registry.MarkDirty(kv.Key);
        }
    }

    private async Task ProcessOneAsync(Transaction t, CancellationToken ct)
    {
        // Always feed the candle aggregator regardless of subscribers â€” the candle
        // service is independent of live-quote subscriptions.
        _candle.OnTransactionTick(t);

        var key = (t.StockId, t.CurrencyType);
        if (!_subs.HasAnySubscribers(key)) return;

        var quote = await _registry.GetOrAddAsync(t.StockId, t.CurrencyType, ct).ConfigureAwait(false);

        if (_subs.HasUiSubscribers(key))
        {
            _dispatcher.Dispatch(() =>
                quote.ApplyTick(t.Price, t.Quantity, TimeHelper.EnsureUtc(t.Timestamp)));
        }
        else
        {
            quote.ApplyTick(t.Price, t.Quantity, TimeHelper.EnsureUtc(t.Timestamp));
        }

        _registry.MarkDirty(key);
    }
}
