using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §refill-throttle engagement telemetry. Default OFF ⇒ zero cost (Enabled gate); draws no RNG and never
/// affects decision order or values, so byte-identical-off is preserved. When <c>Bots:RefillThrottle:Probe</c>
/// is true it counts, per window, how often the gate WIDENED a resisting-side offset and SKIPPED a
/// resisting-side repost (split by side), plus the seeded skip-draws taken (to verify the flag-on draw
/// discipline at runtime, not just in unit tests). The inert-flag trap sank an earlier lever — a first-minute
/// abort gate (widenApplications==0 / skipReposts==0 with the flavor on and ThresholdArm&gt;0) catches a dead
/// lever immediately. Mirrors the <see cref="JumpsProbe"/>/<see cref="ChaserProbe"/> idiom (static, Enabled
/// gate, Interlocked counters). <see cref="Drain"/> snapshots + resets.
/// </summary>
internal static class RefillThrottleProbe
{
    internal static bool Enabled;
    private static long _widenApplications;
    private static long _skipReposts;
    private static long _skipDraws;
    private static long _resistBuySkips;
    private static long _resistSellSkips;

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>Record one resisting-side limit whose offset was widened on a mover. No-op when disabled.</summary>
    internal static void RecordWiden()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _widenApplications);
    }

    /// <summary>Record one seeded skip-repost draw taken (the gate evaluated the probability this tick).</summary>
    internal static void RecordSkipDraw()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _skipDraws);
    }

    /// <summary>Record one resisting-side limit skipped (not re-posted), routed by side.</summary>
    internal static void RecordSkip(bool isBuy)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _skipReposts);
        if (isBuy) Interlocked.Increment(ref _resistBuySkips);
        else       Interlocked.Increment(ref _resistSellSkips);
    }

    /// <summary>Snapshot and reset. All zero when the feature is off / the gate never engaged.</summary>
    internal static (long widenApplications, long skipReposts, long skipDraws,
                     long resistBuySkips, long resistSellSkips) Drain()
        => (Interlocked.Exchange(ref _widenApplications, 0L),
            Interlocked.Exchange(ref _skipReposts, 0L),
            Interlocked.Exchange(ref _skipDraws, 0L),
            Interlocked.Exchange(ref _resistBuySkips, 0L),
            Interlocked.Exchange(ref _resistSellSkips, 0L));
}
