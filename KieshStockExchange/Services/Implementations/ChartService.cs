using System.Collections.ObjectModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

/// <summary>
/// Assembles price data for stock charts.
/// - Historical: aggregates DB Transactions into OHLC candles (gap-fill optional).
/// - Live: streams candles directly from MarketDataService’s ring buffer.
/// MarketDataService remains the single source of truth for “now” (quotes/last price).
/// </summary>
public sealed class ChartService : IChartService
{
    private readonly ILogger<ChartService> _logger;
    private readonly IDataBaseService _db;
    private readonly IMarketDataService _market;

    public ChartService(ILogger<ChartService> logger, IDataBaseService db, IMarketDataService market)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _market = market ?? throw new ArgumentNullException(nameof(market));
    }

    public async Task<IReadOnlyList<Candle>> GetInitialCandlesAsync(
        int stockId,
        CurrencyType currency,
        TimeSpan lookback,
        TimeSpan bucket,
        bool fillGaps = true,
        CancellationToken ct = default)
    {
        var end = TimeHelper.NowUtc();
        var start = end - lookback;
        return await GetCandlesAsync(stockId, currency, start, end, bucket, fillGaps, ct);
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int stockId,
        CurrencyType currency,
        DateTime startUtc,
        DateTime endUtc,
        TimeSpan bucket,
        bool fillGaps = true,
        CancellationToken ct = default)
    {
        if (bucket <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bucket), "Bucket must be positive.");
        if (endUtc <= startUtc)
            return Array.Empty<Candle>();

        // Normalize ranges to UTC and align ends
        startUtc = TimeHelper.FloorToBucketUtc(TimeHelper.EnsureUtc(startUtc), bucket);
        endUtc = TimeHelper.EnsureUtc(endUtc); // open interval end; do not floor to keep tail candle

        // 1) Pull transactions in [start, end) from DB
        var tx = await _db.GetTransactionsByStockIdAndTimeRange(
            stockId, currency, startUtc, endUtc, ct);

        // 2) Aggregate to candles
        var candles = AggregateTransactionsToCandles(
            tx, stockId, startUtc, endUtc, bucket, fillGaps);

        // 3) If no data at all, use MarketDataService as ultimate fallback (source of truth)
        if (candles.Count == 0 && fillGaps)
        {
            try
            {
                var last = await _market.GetLastPriceAsync(stockId, currency, ct);
                if (last > 0m)
                {
                    candles = FillFlatAcrossRange(stockId, startUtc, endUtc, bucket, last);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ChartService fallback to last price failed.");
            }
        }

        return new ReadOnlyCollection<Candle>(candles);
    }

    public async IAsyncEnumerable<Candle> StreamLiveCandlesAsync(
        int stockId,
        CurrencyType currency,
        TimeSpan bucket,
        bool fillGaps,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Ensure the live ring exists & is backfilled with today's history, then subscribe.
        await _market.BuildFromHistoryAsync(stockId, currency, ct);
        await _market.SubscribeAsync(stockId, currency, ct);

        // Pass-through to the market’s candle stream (built from its RingBuffer).
        // This keeps MarketDataService as the single source of truth for “now”.
        await foreach (var c in _market.StreamCandlesAsync(stockId, currency, bucket, fillGaps, ct))
            yield return c;
    }

    public async Task<Candle?> GetLatestCandleAsync(
        int stockId,
        CurrencyType currency,
        TimeSpan bucket,
        CancellationToken ct = default)
    {
        var end = TimeHelper.NowUtc();
        var start = end - bucket;
        var list = await GetCandlesAsync(stockId, currency, start, end, bucket, fillGaps: true, ct);
        return list.Count > 0 ? list[^1] : (Candle?)null;
    }

    // -------------------------
    // Aggregation helpers
    // -------------------------

    private static List<Candle> AggregateTransactionsToCandles(
        List<Transaction> tx,
        int stockId,
        DateTime startUtc,
        DateTime endUtc,
        TimeSpan bucket,
        bool fillGaps)
    {
        var candles = new List<Candle>(capacity: Math.Max(8, (int)((endUtc - startUtc).Ticks / bucket.Ticks) + 1));
        if (tx is null || tx.Count == 0)
            return candles;

        // Ensure chronological order
        tx.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        DateTime? currentBucketStart = null;
        decimal o = 0, h = 0, l = 0, c = 0;

        // Track last close to support flat gap-fill
        decimal? lastClose = null;

        var alignedStart = TimeHelper.FloorToBucketUtc(startUtc, bucket);
        var alignedEndExclusive = TimeHelper.FloorToBucketUtc(endUtc, bucket) + bucket;

        foreach (var t in tx)
        {
            var time = TimeHelper.EnsureUtc(t.Timestamp);
            if (time < alignedStart || time >= alignedEndExclusive)
                continue;

            var bStart = TimeHelper.FloorToBucketUtc(time, bucket);

            if (currentBucketStart is null)
            {
                // If we’re starting after alignedStart and want gaps, prefill with flat candles
                if (fillGaps && alignedStart < bStart)
                {
                    var seed = lastClose ?? t.Price;
                    var gap = alignedStart;
                    while (gap < bStart)
                    {
                        candles.Add(new Candle(stockId, gap, bucket, seed, seed, seed, seed));
                        gap += bucket;
                        lastClose = seed;
                    }
                }

                currentBucketStart = bStart;
                o = h = l = c = t.Price;
            }
            else if (bStart == currentBucketStart.Value)
            {
                // Update OHLC within the same bucket
                c = t.Price;
                if (t.Price > h) h = t.Price;
                if (t.Price < l) l = t.Price;
            }
            else
            {
                // Emit completed candle for previous bucket
                candles.Add(new Candle(stockId, currentBucketStart.Value, bucket, o, h, l, c));
                lastClose = c;

                // Fill any whole-bucket gaps, if requested
                if (fillGaps)
                {
                    var nextGap = currentBucketStart.Value + bucket;
                    while (nextGap < bStart)
                    {
                        candles.Add(new Candle(stockId, nextGap, bucket, lastClose.Value, lastClose.Value, lastClose.Value, lastClose.Value));
                        nextGap += bucket;
                    }
                }

                // Start the new bucket
                currentBucketStart = bStart;
                o = h = l = c = t.Price;
            }
        }

        // Emit last in-progress bucket (if any transactions were seen)
        if (currentBucketStart is not null)
        {
            candles.Add(new Candle(stockId, currentBucketStart.Value, bucket, o, h, l, c));
            var finalClose = c;

            // Tail gap-fill until the aligned end (open interval)
            if (fillGaps)
            {
                var next = currentBucketStart.Value + bucket;
                while (next < alignedEndExclusive)
                {
                    candles.Add(new Candle(stockId, next, bucket, finalClose, finalClose, finalClose, finalClose));
                    next += bucket;
                }
            }
        }

        return candles;
    }

    private static List<Candle> FillFlatAcrossRange(
        int stockId,
        DateTime startUtc,
        DateTime endUtc,
        TimeSpan bucket,
        decimal price)
    {
        var alignedStart = TimeHelper.FloorToBucketUtc(startUtc, bucket);
        var alignedEndExclusive = TimeHelper.FloorToBucketUtc(endUtc, bucket) + bucket;
        var list = new List<Candle>();

        for (var t = alignedStart; t < alignedEndExclusive; t += bucket)
        {
            list.Add(new Candle(stockId, t, bucket, price, price, price, price));
        }
        return list;
    }

    public static Candle New(int stockId, CurrencyType currency, DateTime timestamp,
        TimeSpan resolution, decimal price, int quantity)
    {
        if (stockId <= 0)
            throw new ArgumentOutOfRangeException(nameof(stockId));
        if (!CurrencyHelper.IsSupported(currency))
            throw new ArgumentOutOfRangeException(nameof(currency));
        if (timestamp <= DateTime.MinValue || timestamp > TimeHelper.NowUtc())
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (resolution <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(resolution));
        if (price <= 0m || quantity <= 0)
            throw new ArgumentOutOfRangeException("firstPrice and quantity must be positive.");

        // Ensure UTC + align to bucket start
        var candle = new Candle
        {
            StockId = stockId,
            CurrencyType = currency,
            ResolutionSeconds = (int)resolution.TotalSeconds,
            OpenTime = TimeHelper.FloorToBucketUtc(timestamp, resolution),
        };

        candle.ApplyTrade(price, quantity);    // seed O/H/L/C, Volume, TradeCount
        return candle;
    }

    /// <summary>
    /// Aggregates a list of equal-resolution candles into a single higher-timeframe candle.
    /// All candles must be for the same StockId, CurrencyType, and base ResolutionSeconds.
    /// </summary>
    /// <param name="candles">Candles of the same base resolution (e.g., 5m).</param>
    /// <param name="targetResolutionSeconds">Target resolution in seconds (e.g., 15m = 900).</param>
    /// <param name="requireFullCoverage">
    /// If true, enforces that candles exactly fill one target bucket and are contiguous with no gaps.
    /// If false, aggregates whatever you give it into the target bucket that contains the earliest candle.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when inputs are invalid or incompatible.</exception>
    public static Candle Aggregate(IReadOnlyList<Candle> candles, int targetResolutionSeconds, bool requireFullCoverage = true)
    {
        if (candles is null || candles.Count == 0)
            throw new ArgumentException("Source candles must not be null or empty.", nameof(candles));
        if (targetResolutionSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetResolutionSeconds), "Target resolution must be positive.");

        // Create a sorted copy by OpenTime
        var ordered = candles.OrderBy(c => c.OpenTime).ToList();

        // Validate same stock / currency / base resolution
        var stockId = ordered[0].StockId;
        var currency = ordered[0].CurrencyType;
        var baseRes = ordered[0].ResolutionSeconds;
        var baseSpan = TimeSpan.FromSeconds(baseRes);
        foreach (var c in ordered)
        {
            if (c.StockId != stockId) throw new ArgumentException("All candles must have the same StockId.");
            if (c.CurrencyType != currency) throw new ArgumentException("All candles must have the same CurrencyType.");
            if (c.ResolutionSeconds != baseRes) throw new ArgumentException("All candles must have the same base ResolutionSeconds.");
            if (!c.IsValid()) throw new ArgumentException("All candles must be valid.");
        }

        // Target must be a multiple of base (e.g., 15m from 5m)
        if (targetResolutionSeconds % baseRes != 0)
            throw new ArgumentException($"Target resolution ({targetResolutionSeconds}s) must be a multiple of base ({baseRes}s).");
        int multiple = targetResolutionSeconds / baseRes;
        if (multiple <= 1)
            throw new ArgumentException("Target resolution must be greater than base resolution.");
        if (ordered.Count > multiple)
            throw new ArgumentException($"Too many candles ({ordered.Count}) to fit in one target bucket ({multiple} max).");
        if (requireFullCoverage && ordered.Count != multiple)
            throw new ArgumentException($"Expected exactly {multiple} candles to fill one {targetResolutionSeconds}s bucket, got {ordered.Count}.");

        // Work out the target bucket start (floor earliest to the target bucket)
        var targetSpan = TimeSpan.FromSeconds(targetResolutionSeconds);
        var earliest = ordered[0].OpenTime;
        var bucketOpen = TimeHelper.FloorToBucketUtc(earliest, targetSpan);

        // If required full coverage: enforce exact, contiguous coverage of the target bucket
        if (requireFullCoverage)
        {
            for (int i = 1; i < ordered.Count; i++)
            {
                var expected = ordered[i - 1].OpenTime + baseSpan;
                if (ordered[i].OpenTime != expected)
                    throw new ArgumentException("Candles are not contiguous at the base resolution.");
            }

            var lastExpectedOpen = bucketOpen + targetSpan - baseSpan;
            if (ordered[0].OpenTime != bucketOpen || ordered[^1].OpenTime != lastExpectedOpen)
                throw new ArgumentException("Candles do not align exactly to the target bucket boundaries.");
        }
        else
        {
            // If not requiring full coverage
            var bucketClose = bucketOpen + targetSpan;
            var previous = ordered[0].OpenTime;
            for (int i = 1; i < ordered.Count; i++)
            {
                var current = ordered[i].OpenTime;
                // Ensure all candles fit within the target bucket
                if (current < bucketOpen || current >= bucketClose)
                    throw new ArgumentException("All candles must fit within the target bucket.");

                // Also ensure two candles are not in the same base bucket
                if (previous == current)
                    throw new ArgumentException("Two or more candles fall into the same base resolution bucket.");
                previous = current;
            }

        }

        // Build the higher-timeframe candle
        var candle = new Candle
        {
            StockId = stockId,
            CurrencyType = currency,
            ResolutionSeconds = targetResolutionSeconds,
            OpenTime = bucketOpen,
            Open = ordered[0].Open,
            High = ordered.Max(c => c.High),
            Low = ordered.Min(c => c.Low),
            Close = ordered[^1].Close,
            Volume = ordered.Sum(c => c.Volume),
            TradeCount = ordered.Sum(c => c.TradeCount)
        };

        if (!candle.IsValid())
            throw new InvalidOperationException("Aggregated candle failed validation.");

        return candle;
    }

    /// <summary> Convenience overload that accepts a TimeSpan for the target resolution. </summary>
    public static Candle Aggregate(IReadOnlyList<Candle> candles, TimeSpan targetResolution, bool requireFullCoverage = true)
        => Aggregate(candles, (int)targetResolution.TotalSeconds, requireFullCoverage);
}
