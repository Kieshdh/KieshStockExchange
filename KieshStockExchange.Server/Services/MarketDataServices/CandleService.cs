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

namespace KieshStockExchange.Services.MarketDataServices;

public sealed class CandleService : ICandleService, IDisposable
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

    public CandleService(IDataBaseService db, ILogger<CandleService> logger, IStockService stock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
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

    public async Task BackfillUpwardAsync(IReadOnlyCollection<CurrencyType> currencies, CancellationToken ct = default)
    {
        if (currencies is null || currencies.Count == 0) return;
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var stocks = _stock.All;
        var nowAligned = TimeHelper.NowUtc();
        const int WindowDays = 60;
        var from = nowAligned - TimeSpan.FromDays(WindowDays);

        // Every chart-visible higher resolution is an integer multiple of 5m
        // (15m=3, 1h=12, 4h=48, 1d=288), so we can aggregate them all from a
        // single source. The bot loop keeps 5m subscribed for every stock by
        // default, so this source is the densest and most reliable.
        var source = CandleResolution.FiveMinutes;
        var targets = new[]
        {
            CandleResolution.FifteenMinutes,
            CandleResolution.OneHour,
            CandleResolution.FourHours,
            CandleResolution.OneDay,
        };

        int producedCandles = 0;
        int failedCombos = 0;

        foreach (var s in stocks)
        {
            if (ct.IsCancellationRequested) return;
            foreach (var ccy in currencies)
            {
                if (!_stock.IsListedIn(s.StockId, ccy)) continue;

                List<Candle>? srcSnapshot = null;
                foreach (var target in targets)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        // Fetch the 5m source once per (stock, currency) and reuse
                        // it for all target resolutions — avoids repeated DB scans.
                        if (srcSnapshot is null)
                        {
                            var src = await GetHistoricalCandlesAsync(
                                s.StockId, ccy, source, from, nowAligned, ct, fillGaps: false).ConfigureAwait(false);
                            if (src.Count == 0) break; // no 5m data → nothing to aggregate
                            srcSnapshot = src as List<Candle> ?? new List<Candle>(src);
                        }

                        // requireFullCoverage:false so a missing 5m here-or-there
                        // (e.g. brief outage between sessions) doesn't blow up the
                        // entire pass with "must provide continuous coverage".
                        var aggregated = AggregateMultipleCandles(
                            srcSnapshot, target, requireFullCoverage: false, allowPartialEdges: true);
                        if (aggregated.Count == 0) continue;

                        await _db.UpsertCandlesAsync(aggregated, ct).ConfigureAwait(false);
                        producedCandles += aggregated.Count;
                    }
                    catch (Exception ex)
                    {
                        failedCombos++;
                        _logger.LogDebug(ex,
                            "Upward backfill failed for stock={Stock} ccy={Ccy} 5m->{Target}",
                            s.StockId, ccy, target);
                    }
                }
            }
        }
        _logger.LogInformation(
            "Upward candle backfill: persisted {Candles} candles across 15m/1h/4h/1d ({Window}d window). " +
            "Failed combos: {Failed}.",
            producedCandles, WindowDays, failedCombos);
    }

    // Bottom-up ladder. Each rung is built from the one directly below it; because
    // we cascade lowest→highest, a 5m gap filled from 1m in this pass is itself a
    // source for the 15m fill that follows. Adjacent steps are all integer multiples
    // (60/15, 300/60, 900/300, 3600/900, 14400/3600, 86400/14400).
    private static readonly CandleResolution[] GapLadder =
    {
        CandleResolution.FifteenSeconds, CandleResolution.OneMinute, CandleResolution.FiveMinutes,
        CandleResolution.FifteenMinutes, CandleResolution.OneHour, CandleResolution.FourHours,
        CandleResolution.OneDay,
    };

    public async Task<int> FillCandleGapsAsync(IReadOnlyCollection<CurrencyType> currencies,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (currencies is null || currencies.Count == 0 || toUtc <= fromUtc) return 0;
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);

        int filled = 0;
        int failedCombos = 0;
        foreach (var s in _stock.All)
        {
            if (ct.IsCancellationRequested) break;
            foreach (var ccy in currencies)
            {
                if (!_stock.IsListedIn(s.StockId, ccy)) continue;
                for (int i = 1; i < GapLadder.Length; i++)
                {
                    if (ct.IsCancellationRequested) return filled;
                    try
                    {
                        filled += await FillOneResolutionGapAsync(
                            s.StockId, ccy, source: GapLadder[i - 1], target: GapLadder[i],
                            fromUtc, toUtc, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failedCombos++;
                        _logger.LogDebug(ex, "Gap-fill failed for stock={Stock} ccy={Ccy} {Src}->{Tgt}",
                            s.StockId, ccy, GapLadder[i - 1], GapLadder[i]);
                    }
                }
            }
        }
        _logger.LogInformation(
            "Candle gap-fill: synthesized {Filled} missing candle(s) over {From:u}..{To:u}. Failed combos: {Failed}.",
            filled, fromUtc, toUtc, failedCombos);
        return filled;
    }

    /// <summary>
    /// Fills the <paramref name="target"/>-resolution buckets in [from, to) that have
    /// a finer source candle but no persisted target candle. Reads the source straight
    /// from the DB (so it sees finer rungs already filled earlier in the same cascade),
    /// aggregates lenient (requireFullCoverage:false), and upserts only the absent
    /// buckets — existing candles are never overwritten.
    /// </summary>
    private async Task<int> FillOneResolutionGapAsync(
        int stockId, CurrencyType currency, CandleResolution source, CandleResolution target,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var targetSpan = TimeSpan.FromSeconds((int)target);
        var from = TimeHelper.FloorToBucketUtc(fromUtc, targetSpan);
        var to = TimeHelper.NextBucketBoundaryUtc(toUtc, targetSpan);

        var src = await _db.GetCandlesByStockIdAndTimeRange(
            stockId, currency, TimeSpan.FromSeconds((int)source), from, to, ct).ConfigureAwait(false);
        if (src.Count == 0) return 0;
        src.Sort(static (a, b) => a.OpenTime.CompareTo(b.OpenTime));

        var existing = await _db.GetCandlesByStockIdAndTimeRange(
            stockId, currency, targetSpan, from, to, ct).ConfigureAwait(false);
        var have = new HashSet<DateTime>(existing.Select(c => c.OpenTime));

        var aggregated = AggregateMultipleCandles(src, target, requireFullCoverage: false, allowPartialEdges: true);
        var missing = aggregated.Where(c => !have.Contains(c.OpenTime)).ToList();
        if (missing.Count == 0) return 0;

        await _db.UpsertCandlesAsync(missing, ct).ConfigureAwait(false);
        return missing.Count;
    }

    public async Task PrimeRingsAsync(IReadOnlyCollection<CurrencyType> currencies,
        IReadOnlyCollection<CandleResolution> resolutions, CancellationToken ct = default)
    {
        if (currencies is null || currencies.Count == 0) return;
        if (resolutions is null || resolutions.Count == 0) return;

        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var stocks = _stock.All;
        var nowAligned = TimeHelper.NowUtc();
        int primedFromDb = 0;
        int primedFromReplay = 0;
        int emptyKeys = 0;

        foreach (var s in stocks)
        {
            foreach (var ccy in currencies)
            {
                if (!_stock.IsListedIn(s.StockId, ccy)) continue;

                foreach (var res in resolutions)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var span = TimeSpan.FromSeconds((int)res);
                        // Look back exactly RingCapacity buckets so the DB scan
                        // is narrow and any transaction replay only walks the
                        // window we care about.
                        var to = TimeHelper.NextBucketBoundaryUtc(nowAligned, span);
                        var from = to - span * RingCapacity;

                        // Snapshot what's in the DB first so we can tell whether
                        // GetHistoricalCandlesAsync hit the cached rows or had to
                        // replay transactions to manufacture them. Cheap COUNT-shaped
                        // call would be ideal, but the existing helper returns the
                        // list; we just check whether replay happened by comparing
                        // before/after.
                        var preCount = (await _db.GetCandlesByStockIdAndTimeRange(
                            s.StockId, ccy, span, from, to, ct).ConfigureAwait(false)).Count;

                        // Route through the smart historical method so the
                        // DB → transaction-replay → persist waterfall fires for
                        // keys that have raw trades but no persisted candles yet.
                        var candles = await GetHistoricalCandlesAsync(
                            s.StockId, ccy, res, from, to, ct, fillGaps: false).ConfigureAwait(false);

                        if (candles.Count == 0) { emptyKeys++; continue; }

                        // Candles arrive ascending by OpenTime; push them in that
                        // order so the ring's FIFO iteration matches wall clock.
                        var ring = GetOrAddRing((s.StockId, ccy, res));
                        for (int i = 0; i < candles.Count; i++)
                            ring.Push(candles[i]);

                        if (preCount == 0) primedFromReplay++; else primedFromDb++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ring prime failed for {Stock}/{Ccy}/{Res}", s.StockId, ccy, res);
                    }
                }
            }
        }
        _logger.LogInformation(
            "Candle rings primed: {FromDb} from DB, {FromReplay} via transaction replay, {Empty} keys had no data " +
            "(stocks={Stocks}, currencies={Currencies}, resolutions={Resolutions})",
            primedFromDb, primedFromReplay, emptyKeys,
            stocks.Count, currencies.Count, resolutions.Count);
    }

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

    public async Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(
        int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default, bool fillGaps = false)
    {
        CheckKey(stockId, currency, resolution);

        // Align the range to bucket boundaries to avoid partial first/last buckets
        var span = TimeSpan.FromSeconds((int)resolution);
        var fromAligned = TimeHelper.FloorToBucketUtc(fromUtc, span);
        var toAligned = TimeHelper.NextBucketBoundaryUtc(toUtc, span);

        // Hot-ring fast path: if the per-key ring covers fromAligned, serve
        // entirely from RAM. Chart switches between resolutions hit this for
        // the common "last hour / last day" windows once the ring is warm.
        if (_recent.TryGetValue((stockId, currency, resolution), out var ring))
        {
            var (ringCandles, oldest) = ring.Snapshot(fromAligned, toAligned);
            if (oldest is DateTime o && o <= fromAligned && ringCandles.Count > 0)
            {
                if (!fillGaps) return ringCandles;
                return FillGaps(ringCandles, toAligned, span, stockId, currency);
            }
        }

        // Load from DB and sort by time
        var list = await _db.GetCandlesByStockIdAndTimeRange(stockId, currency,
            span, fromAligned, toAligned, ct).ConfigureAwait(false);
        list.Sort(static (a, b) => a.OpenTime.CompareTo(b.OpenTime));

        // No persisted candles for this resolution: rebuild from transactions and persist them.
        if (list.Count == 0)
        {
            var ticks = await _db.GetTransactionsByStockIdAndTimeRange(
                stockId, currency, fromAligned, toAligned, ct: ct).ConfigureAwait(false);
            if (ticks.Count > 0)
            {
                list = ReplayTicksBuildClosed(stockId, currency, resolution, ticks, toAligned, ct);
                list.Sort(static (a, b) => a.OpenTime.CompareTo(b.OpenTime));
                await PersistAndPublishAsync((stockId, currency, resolution), list, ct).ConfigureAwait(false);
            }
        }

        if (!fillGaps) return list;
        if (list.Count == 0) return list;

        return FillGaps(list, toAligned, span, stockId, currency);
    }

    /// <summary>
    /// Walks the candle list forward, inserting flat-priced fillers at any missing
    /// bucket so the chart can render gapless. Anything before the first real candle
    /// is omitted — the left edge stays the stock's first trade, not a synthesized
    /// pre-history.
    /// </summary>
    private IReadOnlyList<Candle> FillGaps(IReadOnlyList<Candle> list, DateTime toAligned, TimeSpan span,
        int stockId, CurrencyType currency)
    {
        var result = new List<Candle>(list.Count);
        var firstRealOpen = list[0].OpenTime;
        decimal lastPrice = list[0].Open;
        var i = 0;

        for (var t = firstRealOpen; t < toAligned; t = t.Add(span))
        {
            if (i < list.Count && list[i].OpenTime == t)
            {
                var c = list[i++];
                result.Add(c);
                lastPrice = c.Close;
            }
            else
            {
                result.Add(NewCandle(stockId, currency, t, span, lastPrice));
            }
        }
        return result;
    }
    #endregion

    #region Fix historical candles
    public async Task<CandleFixReport> FixCandlesAsync(
        int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var key = (stockId, currency, resolution);
        CheckKey(key);

        // Determine bucket-aligned time range
        var (bucket, from, to) = AlignRange(resolution, fromUtc, toUtc);

        // Load existing candles in range
        var existing = await _db.GetCandlesByStockIdAndTimeRange(
            stockId, currency, bucket, from, to, ct).ConfigureAwait(false);
        var existingByKey = new HashSet<Candle>(existing, CandleKeyComparer.Instance);

        // Load all transactions in range
        var ticks = await _db.GetTransactionsByStockIdAndTimeRange(
            stockId, currency, from, to, ct: ct).ConfigureAwait(false);
        if (ticks.Count == 0) return EmptyCandlesReport(stockId, currency, resolution, fromUtc, toUtc);

        // Build all candles using a temporary aggregator
        var closed = ReplayTicksBuildClosed(stockId, currency, resolution, ticks, toUtc, ct);

        // Find missing candles and persist
        var(missedCount, missedTxCount, firstMissing, lastMissing) = 
            await FindMissingAndPersist(key, existingByKey, closed, ct).ConfigureAwait(false);

        // Fix wrong candles and persist
        var (fixedCount, missedTxCount2) = await FindWrongAndPersist(
            key, existingByKey, closed, ct).ConfigureAwait(false);

        // Return report
        return new CandleFixReport(stockId, currency, resolution, fromUtc, toUtc,
            MissingCandleCount: missedCount, FixedCandleCount: fixedCount,
            MissedTxCount: missedTxCount + missedTxCount2, TotalTxCount: ticks.Count, 
            FirstMissing: firstMissing, LastMissing: lastMissing
        );
    }

    private static (TimeSpan bucket, DateTime from, DateTime to) AlignRange(
        CandleResolution resolution, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            throw new ArgumentException("'toUtc' must be strictly after 'fromUtc'.");
        var bucket = TimeSpan.FromSeconds((int)resolution);
        var from = TimeHelper.FloorToBucketUtc(fromUtc, bucket);
        var to = TimeHelper.NextBucketBoundaryUtc(toUtc, bucket);
        return (bucket, from, to);
    }
    
    private List<Candle> ReplayTicksBuildClosed(
        int stockId, CurrencyType currency, CandleResolution resolution,
        List<Transaction> ticks, DateTime ToUtc, CancellationToken ct)
    {
        // Build using a temporary aggregator
        var agg = new CandleAggregator(stockId, currency, resolution, _logger, true);
        foreach (var t in ticks.OrderBy(t => t.Timestamp))
        {
            if (ct.IsCancellationRequested) break;
            agg.OnTick(t);
        }
        agg.FlushIfElapsed(ToUtc); // Flush everything that fully elapsed before 'toUtc'
        return agg.DrainClosedCandles();
    }

    private async Task<(int MissedCount, int MissedTxCount, DateTime? First, DateTime? Last)> FindMissingAndPersist(
        (int, CurrencyType, CandleResolution) key, HashSet<Candle> existingCandles, List<Candle> allCandles, CancellationToken ct)
    {
        // Find missing candles and persist
        var missing = allCandles.Where(c => !existingCandles.Contains(c)).ToList();

        // Persist missing
        await PersistAndPublishAsync(key, missing, ct).ConfigureAwait(false);

        // Build report data
        DateTime? firstMissing = missing.Count > 0 ? missing.Min(c => c.OpenTime) : null;
        DateTime? lastMissing = missing.Count > 0 ? missing.Max(c => c.OpenTime) : null;
        int missedTxCount = missing.Sum(c => c.TradeCount);
        int missedCount = missing.Count;
        return (missedCount, missedTxCount, firstMissing, lastMissing);
    }

    private async Task<(int FixedCount, int MissedTxCount)> FindWrongAndPersist((int, CurrencyType, CandleResolution) key, 
        HashSet<Candle> existingCandles, List<Candle> allCandles, CancellationToken ct)
    {
        var wrong = new List<Candle>();
        int missedTxCount = 0;

        // Find wrong candles (same key but different OHLCV)
        foreach (var c in allCandles)
        {
            if (existingCandles.TryGetValue(c, out var match) && !CandleFullComparer.Instance.Equals(match, c))
            {
                wrong.Add(c);
                missedTxCount += Math.Max(0, c.TradeCount - match.TradeCount);
            }
        }

        // Persist corrected
        await PersistAndPublishAsync(key, wrong, ct).ConfigureAwait(false);
        return (wrong.Count, missedTxCount);
    }

    private static CandleFixReport EmptyCandlesReport(int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc) =>
        new CandleFixReport(stockId, currency, resolution, fromUtc, toUtc,
            MissingCandleCount: 0, FixedCandleCount: 0,
            MissedTxCount: 0, TotalTxCount: 0,
            FirstMissing: null, LastMissing: null
        );
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
                if (c.IsValid()) batch.Add(c);
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
    public Candle NewCandle(int stockId, CurrencyType currency, DateTime timestamp, TimeSpan resolution, decimal? flatPrice = null)
    {
        if (stockId <= 0)
            throw new ArgumentOutOfRangeException(nameof(stockId));
        if (!CurrencyHelper.IsSupported(currency))
            throw new ArgumentOutOfRangeException(nameof(currency));
        if (timestamp <= DateTime.MinValue || timestamp > TimeHelper.NowUtc())
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (resolution <= TimeSpan.Zero || !Candle.TryFromTimeSpan(resolution, out _))
            throw new ArgumentOutOfRangeException(nameof(resolution));

        // Ensure align to bucket start
        var c = new Candle
        {
            StockId = stockId, CurrencyType = currency,
            BucketSeconds = (int)resolution.TotalSeconds,
            OpenTime = TimeHelper.FloorToBucketUtc(timestamp, resolution),
        };
        // If flat price given, set OHLC to it
        if (flatPrice.HasValue && flatPrice.Value > 0m)
            c.Open = c.High = c.Low = c.Close = flatPrice.Value;
        return c;
    }

    public Candle AggregateCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution, bool requireFullCoverage = true)
    {
        if (candles is null || candles.Count == 0)
            throw new ArgumentException("Source candles must not be null or empty.", nameof(candles));
        var targetBucketSeconds = (int)targetResolution;

        // Create a sorted copy by OpenTime
        var ordered = candles.OrderBy(c => c.OpenTime).ToList();

        // Get key properties from the first candle
        var stockId = ordered[0].StockId; var currency = ordered[0].CurrencyType;
        var baseRes = ordered[0].BucketSeconds; var baseSpan = ordered[0].Bucket;

        // Validate same stock / currency / base resolution / validity
        CheckOrdered(ordered, stockId, currency, baseRes, targetBucketSeconds);
        // Validate ascending coverage and get target bucket open time
        CheckContinuous(ordered, targetBucketSeconds, baseSpan, requireFullCoverage, out var bucketOpenTime);

        // Build the higher-timeframe candle
        var candle = new Candle
        {
            StockId = stockId,                      CurrencyType = currency,
            BucketSeconds = targetBucketSeconds,    OpenTime = bucketOpenTime,
            Open = ordered[0].Open,
            // §bounce vwap: child closes are per-bucket VWAPs, so their volume-weighted mean IS the bucket VWAP.
            Close = Candle.VwapClose ? WeightedClose(ordered) : ordered[^1].Close,
            High = ordered.Max(c => c.High),        Low = ordered.Min(c => c.Low),
            Volume = ordered.Sum(c => c.Volume),    TradeCount = ordered.Sum(c => c.TradeCount),
            MaxTransactionId = ordered.Max(c => c.MaxTransactionId),
            MinTransactionId = ordered.Min(c => c.MinTransactionId),
        };

        if (!candle.IsValid())
            throw new InvalidOperationException("Aggregated candle failed validation.");

        return candle;
    }

    /// <summary>Volume-weighted mean of the child closes; zero total volume (gap-filled flats) ⇒ last close.</summary>
    internal static decimal WeightedClose(IReadOnlyList<Candle> ordered)
    {
        long volume = 0; decimal notional = 0m;
        foreach (var c in ordered)
        {
            volume += c.Volume;
            notional += c.Close * c.Volume;
        }
        return volume > 0 ? notional / volume : ordered[^1].Close;
    }

    public List<Candle> AggregateMultipleCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution, 
        bool requireFullCoverage = true, bool allowPartialEdges = true)
    {
        // Validate input candles
        if (candles is null || candles.Count == 0)
            throw new ArgumentException("Source candles must not be null or empty.", nameof(candles));

        var stockId = candles[0].StockId; var currency = candles[0].CurrencyType;
        var baseRes = candles[0].BucketSeconds; var targetRes = (int)targetResolution;
        CheckOrdered(candles.ToList(), stockId, currency, baseRes, targetRes);

        // Get targetSpan
        var targetSpan = TimeSpan.FromSeconds((int)targetResolution);

        // Group candles by target bucket
        var grouped = candles.OrderBy(c => c.OpenTime)
            .GroupBy(c => TimeHelper.FloorToBucketUtc(c.OpenTime, targetSpan)).OrderBy(g => g.Key)
            .Select(g => new { BucketStart = g.Key, Items = g.ToList() }).ToList();

        // Aggregate each group into one candle and add to result
        var result = new List<Candle>();
        for (int i = 0; i < grouped.Count; i++)
        {
            var group = grouped[i];
            var isEdge = i == 0 || i == grouped.Count - 1;
            var requireFull = requireFullCoverage && (!allowPartialEdges || !isEdge);
            try { result.Add(AggregateCandles(group.Items, targetResolution, requireFull)); }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"Failed to aggregate candles for bucket starting at {group.BucketStart:u}. " +
                    $"RequireFullCoverage={requireFull}, AllowPartialEdges={allowPartialEdges}.", ex);
            }

        }

        return result;
    }

    public async Task<IReadOnlyList<Candle>> AggregateAndPersistRangeAsync(
        int stockId, CurrencyType currency, CandleResolution sourceRes, CandleResolution targetRes,
        DateTime fromUtc, DateTime toUtc, bool allowPartialEdges = true, CancellationToken ct = default)
    {
        // Validate target resolution is a strict multiple of source resolution
        if ((int)targetRes <= (int)sourceRes || (int)targetRes % (int)sourceRes != 0)
            throw new ArgumentOutOfRangeException(nameof(targetRes),
                "Target resolution must be a strict multiple of source resolution.");

        // Load source candles
        var src = await GetHistoricalCandlesAsync(
            stockId, currency, sourceRes, fromUtc, toUtc, ct).ConfigureAwait(false);

        // Aggregate to target resolution
        var aggregated = AggregateMultipleCandles(src, targetRes, true, allowPartialEdges);

        // Persist to DB (UpsertCandlesAsync is atomic server-side)
        await _db.UpsertCandlesAsync(aggregated, ct).ConfigureAwait(false);

        return aggregated;
    }
    #endregion

    #region Checks
    public static void CheckKey(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        if (stockId <= 0)
            throw new ArgumentOutOfRangeException(nameof(stockId));
        if (!CurrencyHelper.IsSupported(currency))
            throw new ArgumentOutOfRangeException(nameof(currency));
        if (!Candle.TryFromSeconds((int)resolution, out _))
            throw new ArgumentOutOfRangeException(nameof(resolution), "Unsupported resolution.");
    }
    
    public static void CheckKey((int, CurrencyType, CandleResolution) key) =>
        CheckKey(key.Item1, key.Item2, key.Item3);

    private static void CheckOrdered(List<Candle> candles, int stockId, CurrencyType currency, int baseRes, int targetRes)
    {
        // Validate same stock / currency / base resolution / validity
        if (!candles.All(c => c.StockId == stockId))
            throw new ArgumentException("All candles must be for the same stock.");
        if (!candles.All(c => c.CurrencyType == currency))
            throw new ArgumentException("All candles must be for the same currency.");
        if (!candles.All(c => c.BucketSeconds == baseRes))
            throw new ArgumentException("All candles must have the same base bucket resolution.");
        if (!candles.All(c => c.IsValid()))
            throw new ArgumentException("All candles must be valid.");
        // Validate target resolution
        if (!Candle.TryFromSeconds(targetRes, out _))
            throw new ArgumentException($"Unsupported target resolution: {targetRes}s.");
        if (targetRes <= baseRes || targetRes % baseRes != 0)
            throw new ArgumentException(
                $"Target resolution ({targetRes}s) must be a strict multiple of base ({baseRes}s).");
    }

    private static void CheckContinuous(List<Candle> candles, int targetBucketSeconds, 
        TimeSpan baseSpan, bool fullCoverage, out DateTime bucketOpen)
    {
        // Work out the target bucket start (floor earliest to the target bucket)
        var targetSpan = TimeSpan.FromSeconds(targetBucketSeconds);
        bucketOpen = TimeHelper.FloorToBucketUtc(candles[0].OpenTime, targetSpan);
        var bucketClose = bucketOpen + targetSpan;

        // If required full coverage: enforce exact, contiguous coverage of the target bucket
        if (fullCoverage)
        {
            var expected = bucketOpen;
            foreach (var candle in candles)
            {
                if (candle.OpenTime != expected)
                    throw new ArgumentException("Candles must provide continuous coverage of the target bucket.");
                expected += baseSpan;
            }
            if (expected != bucketClose)
                throw new ArgumentException("Candles must provide full coverage of the target bucket.");
        }
        else // Check that all candles fit within the target bucket time range and are in ascending order
        {
            var previous = DateTime.MinValue;
            foreach (var candle in candles)
            {
                if (!TimeHelper.InRangeUtc(candle.OpenTime, bucketOpen, bucketClose))
                    throw new ArgumentException("Candles must fit within the target bucket time range.");
                if (candle.OpenTime <= previous)
                    throw new ArgumentException("Candles must be in strictly ascending order by OpenTime.");
                previous = candle.OpenTime;
            }

        }
    }
    #endregion
}