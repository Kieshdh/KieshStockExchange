namespace KieshStockExchange.Helpers;

/// <summary>
/// §3.6 P5 trailing-stop math — pure, side-effect-free, so it's unit-testable in isolation and shared
/// between the watcher (per-tick) and the entry point (arm-time seed). A trailing stop's trigger is
/// derived from a <b>monotonic watermark</b> (the best price seen since arm) and a fixed offset:
///   - sell-trail (protects a long): watermark = running high; trigger = watermark − distance.
///   - buy-trail  (protects a short): watermark = running low;  trigger = watermark + distance.
/// The offset is an absolute price, or — when <c>isPercent</c> — a percentage (0–100) of the watermark.
/// </summary>
public static class TrailMath
{
    /// <summary>Trail distance below/above the watermark: absolute, or <paramref name="offset"/>% of it.</summary>
    public static decimal Distance(decimal watermark, decimal offset, bool isPercent)
        => isPercent ? watermark * (offset / 100m) : offset;

    /// <summary>Effective trigger price for the current watermark (sell = below, buy = above).</summary>
    public static decimal EffectiveStop(decimal watermark, decimal offset, bool isPercent, bool isBuy)
        => isBuy
            ? watermark + Distance(watermark, offset, isPercent)
            : watermark - Distance(watermark, offset, isPercent);

    /// <summary>Ratchet the watermark toward the favorable extreme only — it never retreats.</summary>
    public static decimal Ratchet(decimal watermark, decimal price, bool isBuy)
        => isBuy ? Math.Min(watermark, price) : Math.Max(watermark, price);

    /// <summary>Has price crossed the trigger? sell fires on a drop to/through it, buy on a rise.</summary>
    public static bool Crossed(decimal price, decimal effectiveStop, bool isBuy)
        => isBuy ? price >= effectiveStop : price <= effectiveStop;
}
