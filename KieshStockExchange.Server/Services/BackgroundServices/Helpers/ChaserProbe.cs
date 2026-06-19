using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §exogenous-information chaser liveliness probe. Default OFF ⇒ zero cost (first-statement early-return),
/// draws no RNG and never affects decision order or values, so byte-identical-off is preserved. When
/// <c>Bots:ExogShock:ChaserProbe = true</c> it counts, per window, how many bot-decisions carried a non-zero
/// chase tilt and the NET signed / mean-absolute tilt — the discriminator a soak operator needs to tell
/// "chaser too weak" (small net) from "chaser not trading" (bots ≈ 0) from "chaser fighting the shock"
/// (net wrong sign). Mirrors the <see cref="ImpactHoldProbe"/> idiom (static, Enabled gate, Interlocked
/// counters scaled to micro-units since Interlocked.Add has no double overload). <see cref="Drain"/> snapshots
/// and resets for the periodic log line.
/// </summary>
internal static class ChaserProbe
{
    private const double Scale = 1_000_000.0; // fixed-point micro-units for Interlocked.Add(long)

    internal static bool Enabled;
    private static long _bots;
    private static long _netMicro;
    private static long _absMicro;

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>Record one bot-decision's (non-zero) averaged chase tilt. No-op when disabled.</summary>
    internal static void Record(double signedTilt)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _bots);
        Interlocked.Add(ref _netMicro, (long)Math.Round(signedTilt * Scale));
        Interlocked.Add(ref _absMicro, (long)Math.Round(Math.Abs(signedTilt) * Scale));
    }

    /// <summary>Snapshot and reset; returns the per-window bot count, net signed tilt, and mean |tilt|.</summary>
    internal static (long bots, double netSignedTilt, double meanAbsTilt) Drain()
    {
        long b   = Interlocked.Exchange(ref _bots, 0L);
        long net = Interlocked.Exchange(ref _netMicro, 0L);
        long abs = Interlocked.Exchange(ref _absMicro, 0L);
        return (b, net / Scale, b > 0L ? (abs / Scale) / b : 0.0);
    }
}
