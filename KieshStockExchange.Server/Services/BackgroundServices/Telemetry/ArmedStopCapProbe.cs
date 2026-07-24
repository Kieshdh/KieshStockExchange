using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §source-cap liveliness probe (Bots:ArmedStopCapProbe). Default OFF ⇒ zero cost (Enabled early-return),
/// draws no RNG and never affects decision order or values, so byte-identical-off is preserved. When on it
/// counts how often the per-bot armed-stop cap BLOCKED a new arm vs. how many arms were COUNTED, so a soak
/// operator can confirm the cap is actually firing (a non-zero <c>blocked</c> once the pool reaches the cap)
/// and catch the inert-flag trap where the increment is wired to the wrong path and the cap silently never
/// binds. Mirrors the <see cref="ImpactHoldProbe"/> idiom (static, Enabled gate, Interlocked counters).
/// The live pool total / per-bot max are read from ctx at drain time (not counted here).
/// </summary>
internal static class ArmedStopCapProbe
{
    internal static bool Enabled;
    private static long _blocked;
    private static long _armed;

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>A protective-stop arm was rejected because the bot was at its cap.</summary>
    internal static void RecordBlocked()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _blocked);
    }

    /// <summary>A standalone armed stop was placed and counted into ctx.ArmedStopCount.</summary>
    internal static void RecordArmed()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _armed);
    }

    /// <summary>Snapshot and reset the counters for the 60s log line.</summary>
    internal static (long blocked, long armed) Drain()
        => (Interlocked.Exchange(ref _blocked, 0L), Interlocked.Exchange(ref _armed, 0L));
}
