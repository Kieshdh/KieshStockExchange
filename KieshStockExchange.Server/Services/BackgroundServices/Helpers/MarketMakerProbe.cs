using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §market-maker-cohort telemetry. Default OFF ⇒ zero cost (Enabled gate), draws no RNG and never affects
/// decision order or values, so byte-identical-off is preserved. When <c>Bots:MarketMaker:Probe = true</c> it
/// counts, per soak window and PER SIDE, how many resting MM quotes were (re)placed and their shares + notional.
///
/// The cohort-level <c>mmNetInventory</c> and the fleet-wide <c>netBotInventory</c> "smoking-gun" drain metric are
/// NOT accumulated here per order — they are point-in-time sums computed at the log site (a position walk), so the
/// probe stays a cheap per-placement counter. Mirrors the <see cref="ChaserProbe"/> idiom (static, Enabled gate,
/// Interlocked counters; notional in whole currency units since Interlocked.Add has no decimal overload).
/// <see cref="Drain"/> snapshots+resets.
/// </summary>
internal static class MarketMakerProbe
{
    internal static bool Enabled;
    private static long _bidOrders;
    private static long _askOrders;
    private static long _bidShares;
    private static long _askShares;
    private static long _bidNotional;   // whole currency units
    private static long _askNotional;

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>Record one resting MM quote (re)placed, routed by side. No-op when disabled.</summary>
    internal static void RecordResting(bool isBid, int shares, decimal notional)
    {
        if (!Enabled) return;
        long n = (long)System.Math.Round(notional);
        if (isBid) { Interlocked.Increment(ref _bidOrders); Interlocked.Add(ref _bidShares, shares); Interlocked.Add(ref _bidNotional, n); }
        else       { Interlocked.Increment(ref _askOrders); Interlocked.Add(ref _askShares, shares); Interlocked.Add(ref _askNotional, n); }
    }

    /// <summary>Snapshot and reset the per-side resting-quote counters.</summary>
    internal static (long bidOrders, long askOrders, long bidShares, long askShares,
                     double bidNotional, double askNotional) Drain()
    {
        long bo = Interlocked.Exchange(ref _bidOrders, 0L);
        long ao = Interlocked.Exchange(ref _askOrders, 0L);
        long bsh = Interlocked.Exchange(ref _bidShares, 0L);
        long ash = Interlocked.Exchange(ref _askShares, 0L);
        long bn = Interlocked.Exchange(ref _bidNotional, 0L);
        long an = Interlocked.Exchange(ref _askNotional, 0L);
        return (bo, ao, bsh, ash, bn, an);
    }
}
