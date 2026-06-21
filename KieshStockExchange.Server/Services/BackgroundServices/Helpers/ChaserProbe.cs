using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §direct-flow chaser telemetry. Default OFF ⇒ zero cost (Enabled gate), draws no RNG and never affects
/// decision order or values, so byte-identical-off is preserved. When <c>Bots:ExogShock:ChaserProbe = true</c>
/// it counts, per window and PER SIDE, how many real chase ORDERS were built and their notional, plus how many
/// selected chases were SUPPRESSED (price 0 / cash- or share-clamped to 0) so a flat probe can be told apart
/// from a blocked one.
///
/// §chaser-v2 separability readout: the v2 ratio-fix must drive NET notional (buy − sell) → 0 (drift removed)
/// while keeping GROSS notional (buy + sell) roughly flat vs OFF (the acf-driving volume retained). Tracking the
/// two sides independently lets the soak prove both at once: drift is the first moment of flow, the ret_acf win
/// is its gross volume. Mirrors the <see cref="ImpactHoldProbe"/> idiom (static, Enabled gate, Interlocked
/// counters; notional accumulated in whole currency units since Interlocked.Add has no decimal overload).
/// <see cref="Drain"/> snapshots+resets.
/// </summary>
internal static class ChaserProbe
{
    internal static bool Enabled;
    private static long _buyOrders;
    private static long _sellOrders;
    private static long _buySuppressed;
    private static long _sellSuppressed;
    private static long _buyNotional;   // whole currency units, magnitude (buy side)
    private static long _sellNotional;  // whole currency units, magnitude (sell side)

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>Record one real chase order built (placed into the batch), routed by side. No-op when disabled.</summary>
    internal static void RecordOrder(bool isBuy, decimal notional)
    {
        if (!Enabled) return;
        long n = (long)System.Math.Round(notional);
        if (isBuy) { Interlocked.Increment(ref _buyOrders);  Interlocked.Add(ref _buyNotional,  n); }
        else       { Interlocked.Increment(ref _sellOrders); Interlocked.Add(ref _sellNotional, n); }
    }

    /// <summary>Record one selected chase suppressed before placement (price 0 / qty clamped to 0), routed by side.</summary>
    internal static void RecordSuppressed(bool isBuy)
    {
        if (!Enabled) return;
        if (isBuy) Interlocked.Increment(ref _buySuppressed);
        else       Interlocked.Increment(ref _sellSuppressed);
    }

    /// <summary>
    /// Snapshot and reset. Returns per-side order/suppressed counts and notional, plus the two derived
    /// discriminators: <c>net = buy − sell</c> (drift) and <c>gross = buy + sell</c> (volume / ret_acf driver).
    /// </summary>
    internal static (long buyOrders, long sellOrders, long buySuppressed, long sellSuppressed,
                     double buyNotional, double sellNotional, double netNotional, double grossNotional) Drain()
    {
        long bo = Interlocked.Exchange(ref _buyOrders, 0L);
        long so = Interlocked.Exchange(ref _sellOrders, 0L);
        long bs = Interlocked.Exchange(ref _buySuppressed, 0L);
        long ss = Interlocked.Exchange(ref _sellSuppressed, 0L);
        long bn = Interlocked.Exchange(ref _buyNotional, 0L);
        long sn = Interlocked.Exchange(ref _sellNotional, 0L);
        return (bo, so, bs, ss, bn, sn, bn - sn, bn + sn);
    }
}
