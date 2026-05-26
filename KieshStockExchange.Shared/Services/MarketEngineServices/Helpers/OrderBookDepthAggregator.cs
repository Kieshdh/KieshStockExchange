namespace KieshStockExchange.Services.MarketEngineServices.Helpers;

/// <summary>
/// Pure depth-bucketing helpers extracted from OrderBookViewModel.
/// No I/O, no MAUI deps, no observable state — unit-testable in isolation.
/// </summary>
public static class OrderBookDepthAggregator
{
    /// <summary>
    /// 1-5-10 step progression. Coarsest entries are appended dynamically when
    /// a single bucket already concentrates ≥ 25% of side volume.
    /// </summary>
    public static readonly decimal[] BucketStepCandidates =
        new[] { 0.01m, 0.05m, 0.10m, 0.50m, 1.00m, 5.00m, 10.00m, 50.00m, 100.00m };

    /// <summary>
    /// Collapse adjacent levels that share a bucket floor of <paramref name="step"/>.
    /// Input must be sorted high → low; output keeps the same direction.
    /// Step ≤ 0 returns a copy unchanged.
    /// </summary>
    public static List<DepthLevel> BucketLevels(IReadOnlyList<DepthLevel> orderedHighToLow, decimal step)
    {
        if (step <= 0m || orderedHighToLow.Count == 0)
            return new List<DepthLevel>(orderedHighToLow);

        var result = new List<DepthLevel>(orderedHighToLow.Count);
        foreach (var level in orderedHighToLow)
        {
            var floor = Math.Floor(level.Price / step) * step;
            if (result.Count > 0 && result[^1].Price == floor)
            {
                var prev = result[^1];
                result[^1] = new DepthLevel(floor, prev.Quantity + level.Quantity, prev.OrderCount + level.OrderCount);
            }
            else
            {
                result.Add(new DepthLevel(floor, level.Quantity, level.OrderCount));
            }
        }
        return result;
    }

    /// <summary>
    /// Largest single-bucket volume <paramref name="step"/> would produce for
    /// one side of the book. Used by the picker's 25%-concentration heuristic.
    /// </summary>
    public static long MaxBucketVolumeAt(IReadOnlyList<DepthLevel> levels, decimal step)
    {
        if (levels.Count == 0 || step <= 0m) return 0;
        long max = 0, cur = 0;
        decimal? prevFloor = null;
        foreach (var l in levels)
        {
            var floor = Math.Floor(l.Price / step) * step;
            if (prevFloor.HasValue && prevFloor.Value != floor)
            {
                if (cur > max) max = cur;
                cur = 0;
            }
            cur += l.Quantity;
            prevFloor = floor;
        }
        if (cur > max) max = cur;
        return max;
    }

    /// <summary>
    /// Suggested default step for a picker: targets ~0.1% of <paramref name="price"/>,
    /// snaps to the closest candidate in <paramref name="options"/>. Falls back to
    /// the finest step when no price is known yet.
    /// </summary>
    public static decimal AutoStepForPrice(decimal? price, IReadOnlyList<decimal> options)
    {
        if (!price.HasValue || price.Value <= 0m || options.Count == 0)
            return options.Count > 0 ? options[0] : 0.01m;

        var target = price.Value * 0.001m;
        var best = options[0];
        var bestDist = Math.Abs(best - target);
        for (int i = 1; i < options.Count; i++)
        {
            var d = Math.Abs(options[i] - target);
            if (d < bestDist) { best = options[i]; bestDist = d; }
        }
        return best;
    }
}
