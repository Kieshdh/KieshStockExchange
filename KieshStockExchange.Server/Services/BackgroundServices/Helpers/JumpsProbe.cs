using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §fat-tail jumps telemetry. Default OFF ⇒ zero cost (Enabled gate), draws no RNG and never affects decision
/// order or values, so byte-identical-off is preserved. When <c>Bots:Jumps:Probe = true</c> it counts, per
/// window, how many primary jumps FIRED (and the mean realized move), how many selected jumps were SUPPRESSED
/// (price 0 / aggressor cash- or share-clamped to 0 / book dry), the per-side event counts and notional, and the
/// number of aftershock nudges. The inert-flag trap sank an earlier lever — <c>fired=0</c> here in the first ~1
/// min of a soak catches it immediately. Mirrors the <see cref="ChaserProbe"/>/<see cref="ImpactHoldProbe"/>
/// idiom (static, Enabled gate, Interlocked counters; notional accumulated in whole currency units since
/// Interlocked.Add has no decimal overload). <see cref="Drain"/> snapshots + resets.
/// </summary>
internal static class JumpsProbe
{
    internal static bool Enabled;
    private static long _fired;
    private static long _suppressed;
    private static long _realizedBps;   // Σ realized move in basis points (for the mean); whole-int Interlocked
    private static long _buyEvents;
    private static long _sellEvents;
    private static long _buyNotional;   // whole currency units, magnitude (buy side)
    private static long _sellNotional;  // whole currency units, magnitude (sell side)
    private static long _aftershocks;

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>Record one primary jump that realized (≥1 slice filled), routed by side. No-op when disabled.</summary>
    internal static void RecordJump(bool isBuy, double realizedPct, decimal grossNotional)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _fired);
        Interlocked.Add(ref _realizedBps, (long)System.Math.Round(realizedPct * 10000.0));
        long n = (long)System.Math.Round(grossNotional);
        if (isBuy) { Interlocked.Increment(ref _buyEvents);  Interlocked.Add(ref _buyNotional,  n); }
        else       { Interlocked.Increment(ref _sellEvents); Interlocked.Add(ref _sellNotional, n); }
    }

    /// <summary>Record one selected jump that produced no flow (price 0 / clamped to 0 / book dry).</summary>
    internal static void RecordSuppressed()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _suppressed);
    }

    /// <summary>Record one aftershock nudge that filled (sustains volatility clustering after the primary jump).</summary>
    internal static void RecordAftershock()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _aftershocks);
    }

    /// <summary>
    /// Snapshot and reset. <c>net = buy − sell</c> (drift), <c>gross = buy + sell</c> (event volume), and the
    /// fired-weighted mean realized move (fraction). All zero when the feature is off / nothing fired.
    /// </summary>
    internal static (long fired, long suppressed, double meanPct, long buyEvents, long sellEvents,
                     double netNotional, double grossNotional, long aftershocks) Drain()
    {
        long f  = Interlocked.Exchange(ref _fired, 0L);
        long su = Interlocked.Exchange(ref _suppressed, 0L);
        long rb = Interlocked.Exchange(ref _realizedBps, 0L);
        long be = Interlocked.Exchange(ref _buyEvents, 0L);
        long se = Interlocked.Exchange(ref _sellEvents, 0L);
        long bn = Interlocked.Exchange(ref _buyNotional, 0L);
        long sn = Interlocked.Exchange(ref _sellNotional, 0L);
        long af = Interlocked.Exchange(ref _aftershocks, 0L);
        double meanPct = f > 0L ? (rb / (double)f) / 10000.0 : 0.0;
        return (f, su, meanPct, be, se, bn - sn, bn + sn, af);
    }
}
