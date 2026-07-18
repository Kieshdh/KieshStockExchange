using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class CandleService : ICandleService, IDisposable
{
    #region Aggregator State and Subscriptions
    // Candle aggregators
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleAggregator> _aggs = new();
    public ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleAggregator> Aggregators => _aggs;

    // Per-book aggregator index: avoids the O(N_aggs) LINQ scan in OnTransactionTickAsync.
    // Rebuilt on Subscribe / Unsubscribe (rare); read on every tick (hot).
    private readonly ConcurrentDictionary<(int, CurrencyType), CandleAggregator[]> _aggsByBook = new();

    // Subscription reference counting
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), int> _subRefCount = new();

    // Cached snapshot of subscribed keys; rebuilt on Subscribe/Unsubscribe to avoid
    // per-read LINQ allocations (PriceSnapshotService reads this hourly).
    private volatile IReadOnlyCollection<(int, CurrencyType, CandleResolution)> _subscribedSnapshot =
        Array.Empty<(int, CurrencyType, CandleResolution)>();
    public IReadOnlyCollection<(int, CurrencyType, CandleResolution)> Subscribed => _subscribedSnapshot;

    // Hot ring of last N closed candles per key. Filled by the flush loop after
    // a bucket closes; served from RAM by GetCandlesInRangeAsync when the
    // requested window fits inside the ring. Avoids a DB range scan on every
    // chart switch.
    private const int RingCapacity = 500;
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleRingBuffer> _recent = new();

    // Live candle streams
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), Channel<Candle>> _streams = new();
    private CancellationTokenSource _flushCts = new();
    private Task? _flushLoop;

    // Flush interval and Default Candle Resolution
    public TimeSpan FlushInterval { get; } = TimeSpan.FromSeconds(1);

    public CandleResolution DefaultCandleResolution { get; private set; } = CandleResolution.Default;

    /// <summary>
    /// Raised once per live bucket close (flush loop, phase 3). MarketHubBroadcaster
    /// subscribes to broadcast onto quotes:{stockId}:{currency}. Historical paths
    /// (FixCandlesAsync, GetHistoricalCandlesAsync rebuild) deliberately skip this —
    /// they're backfills, not live closes.
    /// </summary>
    public event EventHandler<Candle>? CandleClosed;
    #endregion

    #region Fields and Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<CandleService> _logger;
    private readonly IStockService _stock;
    // §fear-greed: shared singleton (also injected into AiTradeService). Read at flush to stamp the composite
    // onto each closed candle. Depends only on IStockService + IConfiguration ⇒ no DI cycle back into CandleService.
    private readonly MarketMoodService _mood;

    public CandleService(IDataBaseService db, ILogger<CandleService> logger, IStockService stock, MarketMoodService mood)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
        _mood = mood ?? throw new ArgumentNullException(nameof(mood));
    }
    #endregion

    #region Subscriptions and Dispose
    public void Subscribe(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        var key = (stockId, currency, resolution);
        CheckKey(key);

        var newCount = _subRefCount.AddOrUpdate(key, 1, (_, c) => c + 1);
        GetOrAddAggregator(stockId, currency, resolution);
        // _streams is created lazily by StreamClosedCandles — bot-driven candle subs
        // that never open a chart pay zero per-tick channel-write cost.
        RebuildAggsByBook(stockId, currency);
        if (newCount == 1) RebuildSubscribedSnapshot();

        // Start the flush loop if not already running
        if (_flushLoop is null || _flushLoop.IsCompleted)
        {
            if (_flushCts.IsCancellationRequested)
                _flushCts = new CancellationTokenSource();
            _flushLoop = Task.Run(() => FlushLoopAsync(_flushCts.Token));
        }
    }

    public async Task UnsubscribeAsync(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct = default)
    {
        var key = (stockId, currency, resolution);
        CheckKey(key);

        var count = _subRefCount.AddOrUpdate(key, 0, (_, c) => Math.Max(0, c - 1));
        if (count == 0)
        {
            _subRefCount.TryRemove(key, out _);
            _aggs.TryRemove(key, out var agg);
            if (_streams.TryRemove(key, out var stream))
                stream.Writer.TryComplete();
            RebuildAggsByBook(stockId, currency);
            RebuildSubscribedSnapshot();
        }

        if (_subRefCount.IsEmpty && _flushLoop is not null)
        {
            _flushCts.Cancel();
            try { await _flushLoop.ConfigureAwait(false); } catch { } // Ignore
            _flushLoop = null;
        }

    }

    public async Task SubscribeAllAsync(CurrencyType currency, CandleResolution resolution, CancellationToken ct = default)
    {
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        // Subscribe is synchronous; no need to wrap in Task.Run/WhenAll.
        foreach (var s in _stock.All)
        {
            ct.ThrowIfCancellationRequested();
            Subscribe(s.StockId, currency, resolution);
        }
    }

    public async Task SubscribeAllDefaultAsync(CurrencyType currency, CancellationToken ct = default)
        => await SubscribeAllAsync(currency, DefaultCandleResolution, ct).ConfigureAwait(false);

    public void Dispose()
    {
        _flushCts.Cancel();
        if (_flushLoop is not null)
            try { _flushLoop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        foreach (var ch in _streams.Values)
            ch.Writer.TryComplete();
        _aggs.Clear();
        _aggsByBook.Clear();
        _streams.Clear();
        _subRefCount.Clear();
    }
    #endregion

    #region Public methods
    public Task OnTransactionTickAsync(Transaction tick, CancellationToken ct = default)
    {
        OnTransactionTick(tick);
        return Task.CompletedTask;
    }

    public void OnTransactionTick(Transaction tick)
    {
        if (!tick.IsValid()) return;

        // O(1) per-book lookup; no LINQ, no allocation on the hot path.
        if (!_aggsByBook.TryGetValue((tick.StockId, tick.CurrencyType), out var aggs)
            || aggs.Length == 0)
            return;

        for (int i = 0; i < aggs.Length; i++)
        {
            var agg = aggs[i];

            // Update in-memory candle state (O(1)).
            agg.OnTick(tick);

            // Push live snapshot to the chart stream (no DB). TryGetLiveSnapshot allocates,
            // so guard it behind the channel existence check — bot-only books skip the alloc.
            var key = (agg.StockId, agg.Currency, agg.Resolution);
            if (_streams.TryGetValue(key, out var stream))
            {
                var live = agg.TryGetLiveSnapshot();
                if (live is not null)
                    stream.Writer.TryWrite(live);
            }

            // Closed candles are deliberately NOT drained here. FlushLoopAsync runs every
            // FlushInterval (1s) and persists them in a single batched DB transaction.
            // This keeps the hot-path tick handler off the DB.
        }
    }

    public async IAsyncEnumerable<Candle> StreamClosedCandles(int stockId, CurrencyType currency,
        CandleResolution resolution, [EnumeratorCancellation] CancellationToken ct)
    {
        // Ensure subscribed
        var key = (stockId, currency, resolution);
        CheckKey(key);

        // Subscribe to the stream
        Subscribe(stockId, currency, resolution);

        // Get the stream channel
        var stream = GetOrAddLiveStream(key);
        try
        {
            // Send the current live snapshot first if any
            if (_aggs.TryGetValue(key, out var agg))
            {
                var live = agg.TryGetLiveSnapshot();
                if (live is not null)
                    stream.Writer.TryWrite(live);
            }

            // Drain via WaitToReadAsync + TryRead so cancellation exits the loop
            // with yield break instead of throwing OCE through the iterator.
            while (await stream.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) yield break;
                while (stream.Reader.TryRead(out var candle))
                {
                    if (ct.IsCancellationRequested) yield break;
                    yield return candle;
                }
            }
        }
        finally 
        {
            // Unsubscribe when done
            await UnsubscribeAsync(stockId, currency, resolution).ConfigureAwait(false); 
        }
    }

    public Candle? TryGetLiveSnapshot(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        var key = (stockId, currency, resolution);
        CheckKey(key);

        _aggs.TryGetValue(key, out var agg);
        return agg?.TryGetLiveSnapshot();
    }

    #endregion

    #region Fix historical candles
    #endregion

    #region Private helpers
    private CandleAggregator GetOrAddAggregator(int stockId, CurrencyType currency, CandleResolution resolution) =>
        _aggs.GetOrAdd((stockId, currency, resolution), key =>
            new CandleAggregator(stockId, currency, resolution, _logger));

    private CandleRingBuffer GetOrAddRing((int, CurrencyType, CandleResolution) key) =>
        _recent.GetOrAdd(key, _ => new CandleRingBuffer(RingCapacity));

    private Channel<Candle> GetOrAddLiveStream((int, CurrencyType, CandleResolution) key) =>
        _streams.GetOrAdd(key, _ => Channel.CreateBounded<Candle>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            }));

    private async Task PersistAndPublishAsync((int, CurrencyType, CandleResolution) key,
        List<Candle> candles, CancellationToken ct)
    {
        if (candles is null || candles.Count == 0) return;
        try
        {
            // validate
            foreach (var c in candles)
                if (!c.IsValid())
                    throw new InvalidOperationException($"Invalid candle {c.Summary}");

            // Persist to DB — single tx, batched ON CONFLICT upsert.
            await _db.UpsertCandlesAsync(candles, ct).ConfigureAwait(false);

            // Publish to live stream if any subscribers
            if (_streams.TryGetValue(key, out var stream))
            {
                foreach (var candle in candles)
                {
                    if (ct.IsCancellationRequested) break;
                    // Use TryWrite to avoid blocking if no readers
                    stream.Writer.TryWrite(candle);
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error persisting or publishing candles for {Key}", key); }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        // Close buckets even when no fresh ticks arrive.
        using var timer = new PeriodicTimer(FlushInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await FlushClosedCandlesAsync(TimeHelper.NowUtc(), publish: true).ConfigureAwait(false);
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { } // shutdown — fall through to final drain
            catch (Exception ex) { _logger.LogError(ex, "Error in candle flush loop."); }
        }

        // Final drain: buckets that closed since the last tick are still in the
        // aggregators. Persist them on the way out (no publish — no live consumers
        // during shutdown) so a clean stop doesn't leave a hole in history.
        try { await FlushClosedCandlesAsync(TimeHelper.NowUtc(), publish: false).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Error in final candle flush on shutdown."); }
    }

    /// <summary>
    /// Drains every aggregator's closed candles, persists them, and (when
    /// <paramref name="publish"/>) caches + streams them. Persistence always uses
    /// CancellationToken.None: once a candle is drained from its aggregator it lives
    /// only here, so abandoning the write on cancellation would lose it for good.
    /// </summary>
    private async Task FlushClosedCandlesAsync(DateTime now, bool publish)
    {
        // Phase 1: drain every aggregator's closed candles (in-memory only).
        var perKeyClosed = new List<((int, CurrencyType, CandleResolution) key, List<Candle> closed)>();
        foreach (var (key, agg) in _aggs)
        {
            agg.FlushIfElapsed(now);
            var closed = agg.DrainClosedCandles();
            if (closed.Count > 0) perKeyClosed.Add((key, closed));
        }
        if (perKeyClosed.Count == 0) return;

        // Phase 2: flatten valid candles across all keys into one batch — one
        // ON CONFLICT upsert per candle, no per-candle SELECT.
        var batch = new List<Candle>();
        foreach (var (_, closed) in perKeyClosed)
        {
            for (int i = 0; i < closed.Count; i++)
            {
                var c = closed[i];
                if (c.IsValid())
                {
                    // §fear-greed: stamp the composite ONLY when the gauge is on (else leave null — the column
                    // stays honest/unpopulated until the composite is flipped on and soak-validated).
                    if (_mood.Enabled)
                    {
                        // §per-timeframe: stamp all three bands — MarketMood = the band this base resolution
                        // DISPLAYS (BandForBucket); MoodMid/MoodSlow carry the slower horizons forward.
                        int sid = c.StockId;
                        c.MarketMood = _mood.MoodForBand(sid, MarketMoodService.BandForBucket(c.BucketSeconds));
                        c.MoodMid    = _mood.MoodForBand(sid, MarketMoodService.BandMid);
                        c.MoodSlow   = _mood.MoodForBand(sid, MarketMoodService.BandSlow);
                    }
                    batch.Add(c);
                }
                else _logger.LogError("Dropping invalid candle in flush loop: {Summary}", c.Summary);
            }
        }

        if (batch.Count > 0)
        {
            try
            {
                // None, not the loop token: drained candles must survive a shutdown.
                await _db.UpsertCandlesAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting closed candles in flush loop.");
            }
        }

        // Phase 3: cache closed candles in the per-key hot ring, publish to live
        // streams, and raise CandleClosed for the hub. Skipped during shutdown.
        if (!publish) return;
        foreach (var (key, closed) in perKeyClosed)
        {
            _streams.TryGetValue(key, out var stream);
            var ring = GetOrAddRing(key);
            for (int i = 0; i < closed.Count; i++)
            {
                var candle = closed[i];
                ring.Push(candle);
                stream?.Writer.TryWrite(candle);
                try { CandleClosed?.Invoke(this, candle); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CandleClosed handler threw for {Summary}", candle.Summary);
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds the per-book aggregator index for one (stockId, currency) pair after
    /// Subscribe/Unsubscribe. The hot path (OnTransactionTickAsync) reads this array
    /// without LINQ or allocation.
    /// </summary>
    private void RebuildAggsByBook(int stockId, CurrencyType currency)
    {
        var book = (stockId, currency);
        var matches = new List<CandleAggregator>(2);
        foreach (var kv in _aggs)
        {
            if (kv.Key.Item1 == stockId && kv.Key.Item2 == currency)
                matches.Add(kv.Value);
        }

        if (matches.Count == 0)
            _aggsByBook.TryRemove(book, out _);
        else
            _aggsByBook[book] = matches.ToArray();
    }

    private void RebuildSubscribedSnapshot()
    {
        var list = new List<(int, CurrencyType, CandleResolution)>(_subRefCount.Count);
        foreach (var kv in _subRefCount)
            if (kv.Value > 0) list.Add(kv.Key);
        _subscribedSnapshot = list;
    }

    #endregion

    #region Candle Creation and Aggregation
    #endregion

    #region Checks
    #endregion
}