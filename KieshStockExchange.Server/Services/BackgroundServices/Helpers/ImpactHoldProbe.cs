using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §impact-decouple B liveliness probe. Default OFF ⇒ zero cost (first-statement early-return), draws no
/// RNG and never affects decision order or values, so byte-identical-off is preserved. When
/// <c>Bots:ImpactHoldProbe = true</c> it counts held vs recomputed reaction stances so a soak operator can
/// confirm Mechanism B is actually firing — <c>heldFrac ≈ 0</c> means the hold window math is broken and the
/// mechanism is inert. Mirrors the <see cref="BotDecisionProbe"/> idiom (static, Enabled gate, Interlocked
/// counters). Counters are process-wide; <see cref="Drain"/> snapshots and resets them for the periodic log.
/// </summary>
internal static class ImpactHoldProbe
{
    internal static bool Enabled;
    private static long _held;
    private static long _recomputed;

    internal static void Configure(bool enabled) => Enabled = enabled;

    internal static void Record(bool held)
    {
        if (!Enabled) return;
        if (held) Interlocked.Increment(ref _held);
        else      Interlocked.Increment(ref _recomputed);
    }

    /// <summary>Snapshot and reset the counters; returns counts and the held fraction for the 60s log line.</summary>
    internal static (long held, long recomputed, double heldFrac) Drain()
    {
        long h = Interlocked.Exchange(ref _held, 0L);
        long r = Interlocked.Exchange(ref _recomputed, 0L);
        long t = h + r;
        return (h, r, t > 0L ? (double)h / t : 0.0);
    }
}
