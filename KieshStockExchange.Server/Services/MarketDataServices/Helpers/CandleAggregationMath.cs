using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Generic;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>Pure aggregation math + validation helpers shared by the CandleService partials.</summary>
internal static class CandleAggregationMath
{
    internal static (TimeSpan bucket, DateTime from, DateTime to) AlignRange(
        CandleResolution resolution, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            throw new ArgumentException("'toUtc' must be strictly after 'fromUtc'.");
        var bucket = TimeSpan.FromSeconds((int)resolution);
        var from = TimeHelper.FloorToBucketUtc(fromUtc, bucket);
        var to = TimeHelper.NextBucketBoundaryUtc(toUtc, bucket);
        return (bucket, from, to);
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

    internal static void CheckOrdered(List<Candle> candles, int stockId, CurrencyType currency, int baseRes, int targetRes)
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

    internal static void CheckContinuous(List<Candle> candles, int targetBucketSeconds,
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
}
