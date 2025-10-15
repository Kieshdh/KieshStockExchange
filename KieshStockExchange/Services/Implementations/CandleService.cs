using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace KieshStockExchange.Services.Implementations;

public sealed class CandleService : ICandleService, IDisposable
{
    #region Dictionaries keyed by (stockId, currency, bucketSec), with public accessors
    // Candle aggregators
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleAggregator> _aggs = new();
    public ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleAggregator> Aggregators => _aggs;

    // Subscription reference counting
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), int> _subRefCount = new();
    public IReadOnlyCollection<(int, CurrencyType, CandleResolution)> Subscribed =>
        _subRefCount.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList().AsReadOnly();

    // Live candle streams
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), Channel<Candle>> _streams = new();
    private CancellationTokenSource _flushCts = new();
    private Task? _flushLoop;

    public TimeSpan FlushInterval { get; } = TimeSpan.FromSeconds(1);
    #endregion

    #region Fields and Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<CandleService> _logger;

    public CandleService(IDataBaseService db, ILogger<CandleService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Subscriptions and Dispose
    public void Subscribe(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        var key = (stockId, currency, resolution);
        CheckKey(key);

        _subRefCount.AddOrUpdate(key, 1, (_, c) => c + 1);
        GetOrAddAggregator(stockId, currency, resolution);
        GetOrAddLiveStream(key);

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
        }

        if (_subRefCount.IsEmpty && _flushLoop is not null)
        {
            _flushCts.Cancel();
            try { await _flushLoop; } catch { } // Ignore
            _flushLoop = null;
        }

    }

    public async Task SubscribeAllAsync(CurrencyType currency, CandleResolution resolution, CancellationToken ct = default)
    {
        var stocks = await _db.GetStocksAsync(ct);
        await Task.WhenAll(stocks.Select(s => Task.Run(() => Subscribe(s.StockId, currency, resolution), ct)));
    }

    public async Task SubscribeAllDefaultAsync(CurrencyType currency, CancellationToken ct = default)
        => await SubscribeAllAsync(currency, CandleResolution.Default, ct);

    public void Dispose()
    {
        _flushCts.Cancel();
        if (_flushLoop is not null) 
            try { _flushLoop.Wait(); } catch { }
        foreach (var ch in _streams.Values) 
            ch.Writer.TryComplete();
        _aggs.Clear();
        _streams.Clear();
        _subRefCount.Clear();
    }
    #endregion

    #region Public methods
    public async Task OnTransactionTickAsync(Transaction tick, CancellationToken ct = default)
    {
        if (!tick.IsValid()) return;

        // Find matching aggregators
        var stockId = tick.StockId; var currency = tick.CurrencyType;
        var matching = _aggs.Keys.Where(k => k.Item1 == stockId && k.Item2 == currency).ToArray();

        // Apply the tick to each matching aggregator
        foreach (var key in matching)
        {
            // Should always succeed as we got the key from the dictionary
            if (!_aggs.TryGetValue(key, out var agg)) 
                continue;

            // Apply the tick
            agg.OnTick(tick);

            // If any closed candles, persist and publish
            var closed = agg.DrainClosedCandles();
            await PersistAndPublishAsync(key, closed, ct);
        }
    }

    public async IAsyncEnumerable<Candle> StreamClosedCandles(int stockId, CurrencyType currency,
        CandleResolution resolution, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = (stockId, currency, resolution);
        CheckKey(key);

        Subscribe(stockId, currency, resolution);
        var stream = GetOrAddLiveStream(key);
        try
        {
            await foreach (var candle in stream.Reader.ReadAllAsync(ct))
                yield return candle;
        }
        finally { await UnsubscribeAsync(stockId, currency, resolution); }
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
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        CheckKey(stockId, currency, resolution);

        // Align the range to bucket boundaries to avoid partial first/last buckets
        var span = TimeSpan.FromSeconds((int)resolution);
        var fromAligned = TimeHelper.FloorToBucketUtc(fromUtc, span);
        var toAligned = TimeHelper.NextBucketBoundaryUtc(toUtc, span);

        var list = await _db.GetCandlesByStockIdAndTimeRange(
            stockId, currency, span, fromAligned, toAligned, ct);

        return list.OrderBy(c => c.OpenTime).ToList();
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
        var existing = await _db.GetCandlesByStockIdAndTimeRange(stockId, currency, bucket, from, to, ct);
        var existingByKey = new HashSet<Candle>(existing, CandleKeyComparer.Instance);

        // Load all transactions in range
        var ticks = await _db.GetTransactionsByStockIdAndTimeRange(stockId, currency, from, to, ct);
        if (ticks.Count == 0) return EmptyCandlesReport(stockId, currency, resolution, fromUtc, toUtc);

        // Build all candles using a temporary aggregator
        var closed = ReplayTicksBuildClosed(stockId, currency, resolution, ticks, toUtc, ct);

        // Find missing candles and persist
        var(missedCount, missedTxCount, firstMissing, lastMissing) = await FindMissingAndPersist(key, existingByKey, closed, ct);

        // Fix wrong candles and persist
        var (fixedCount, missedTxCount2) = await FindWrongAndPersist(key, existingByKey, closed, ct);

        // Return report
        return new CandleFixReport(stockId, currency, resolution, fromUtc, toUtc,
            MissingCandleCount: missedCount, FixedCandleCount: fixedCount,
            MissedTxCount: (missedTxCount + missedTxCount2), TotalTxCount: ticks.Count, 
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
        await PersistAndPublishAsync(key, missing, ct);

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
        await PersistAndPublishAsync(key, wrong, ct);
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

    private Channel<Candle> GetOrAddLiveStream((int, CurrencyType, CandleResolution) key) =>
        _streams.GetOrAdd(key, _ => Channel.CreateUnbounded<Candle>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));

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

            // Persist to DB
            await _db.RunInTransactionAsync(async txCt =>
            {
                foreach (var c in candles)
                    await _db.UpsertCandle(c, txCt);
            }, ct);

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
                var now = TimeHelper.NowUtc();
                foreach (var (key, agg) in _aggs)
                {
                    if (ct.IsCancellationRequested) break;
                    agg.FlushIfElapsed(now);
                    var closed = agg.DrainClosedCandles();
                    await PersistAndPublishAsync(key, closed, ct);
                }
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException) { } // Ignore
            catch (Exception ex) { _logger.LogError(ex, "Error in candle flush loop."); }
        }
            
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
            Open = ordered[0].Open,                 Close = ordered[^1].Close,
            High = ordered.Max(c => c.High),        Low = ordered.Min(c => c.Low),
            Volume = ordered.Sum(c => c.Volume),    TradeCount = ordered.Sum(c => c.TradeCount),
            MaxTransactionId = ordered.Max(c => c.MaxTransactionId),
            MinTransactionId = ordered.Min(c => c.MinTransactionId),
        };

        if (!candle.IsValid())
            throw new InvalidOperationException("Aggregated candle failed validation.");

        return candle;
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
            var isEdge = i == 0 || i == (grouped.Count - 1);
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
        if ((int)targetRes <= (int)sourceRes || ((int)targetRes % (int)sourceRes) != 0)
            throw new ArgumentOutOfRangeException(nameof(targetRes),
                "Target resolution must be a strict multiple of source resolution.");

        // Load source candles
        var src = await GetHistoricalCandlesAsync(stockId, currency, sourceRes, fromUtc, toUtc, ct);

        // Aggregate to target resolution
        var aggregated = AggregateMultipleCandles(src, targetRes, true, allowPartialEdges);

        // Persist to DB
        await _db.RunInTransactionAsync(async tx =>
        {
            foreach (var c in aggregated)
                await _db.UpsertCandle(c, tx);
        }, ct);

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
        if (targetRes <= baseRes || (targetRes % baseRes) != 0)
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