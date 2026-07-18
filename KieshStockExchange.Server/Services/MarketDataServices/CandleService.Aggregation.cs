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

public sealed partial class CandleService
{
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
        CandleAggregationMath.CheckOrdered(ordered, stockId, currency, baseRes, targetBucketSeconds);
        // Validate ascending coverage and get target bucket open time
        CandleAggregationMath.CheckContinuous(ordered, targetBucketSeconds, baseSpan, requireFullCoverage, out var bucketOpenTime);

        // §per-timeframe: the DISPLAYED band depends on the TARGET resolution — pick MarketMood/MoodMid/MoodSlow
        // from the last child by BandForBucket; the band columns are carried forward so the client stays unchanged.
        int band = MarketMoodService.BandForBucket(targetBucketSeconds);
        double? displayMood = band == MarketMoodService.BandMid  ? ordered[^1].MoodMid
                            : band == MarketMoodService.BandSlow ? ordered[^1].MoodSlow
                            : ordered[^1].MarketMood;

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
            // §fear-greed: mood is a LEVEL (like Close) — carry the last child's values into the aggregate.
            MarketMood = displayMood, MoodMid = ordered[^1].MoodMid, MoodSlow = ordered[^1].MoodSlow,
        };

        if (!candle.IsValid())
            throw new InvalidOperationException("Aggregated candle failed validation.");

        return candle;
    }

    internal static decimal WeightedClose(IReadOnlyList<Candle> orderedChildren) => CandleAggregationMath.WeightedClose(orderedChildren);

    public List<Candle> AggregateMultipleCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution, 
        bool requireFullCoverage = true, bool allowPartialEdges = true)
    {
        // Validate input candles
        if (candles is null || candles.Count == 0)
            throw new ArgumentException("Source candles must not be null or empty.", nameof(candles));

        var stockId = candles[0].StockId; var currency = candles[0].CurrencyType;
        var baseRes = candles[0].BucketSeconds; var targetRes = (int)targetResolution;
        CandleAggregationMath.CheckOrdered(candles.ToList(), stockId, currency, baseRes, targetRes);

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
}
