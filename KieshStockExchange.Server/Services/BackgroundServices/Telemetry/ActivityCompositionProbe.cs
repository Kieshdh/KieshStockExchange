using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §composition taker-override engagement telemetry. Counts, per window, how many decisions were eligible
/// for the activity→taker override and how many actually converted (limit→taker upgrades in hot regimes,
/// taker→limit downgrades in quiet ones, split by side). The inert-flag trap sank a prior lever — a
/// first-minutes eligible==0 / up+down==0 read with the exponent &gt;0 catches a dead coupling immediately,
/// and a sustained one-sided up/down split is the drift-lean tell. Mirrors the
/// <see cref="RefillThrottleProbe"/> idiom (static, Enabled gate, Interlocked counters, Drain snapshots+resets).
/// </summary>
internal static class ActivityCompositionProbe
{
    internal static bool Enabled;
    private static long _eligible;
    private static long _upgrades;
    private static long _downgrades;
    private static long _buyUpgrades;
    private static long _sellUpgrades;

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>Record one decision that reached the override gate (convertible type, lever on).</summary>
    internal static void RecordEligible()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _eligible);
    }

    /// <summary>Record one limit→taker upgrade (hot regime), routed by side.</summary>
    internal static void RecordUpgrade(bool isBuy)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _upgrades);
        if (isBuy) Interlocked.Increment(ref _buyUpgrades);
        else       Interlocked.Increment(ref _sellUpgrades);
    }

    /// <summary>Record one taker→limit downgrade (quiet regime).</summary>
    internal static void RecordDowngrade()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _downgrades);
    }

    /// <summary>Snapshot and reset. All zero when the lever is off / never engaged.</summary>
    internal static (long eligible, long upgrades, long downgrades, long buyUpgrades, long sellUpgrades) Drain()
        => (Interlocked.Exchange(ref _eligible, 0L),
            Interlocked.Exchange(ref _upgrades, 0L),
            Interlocked.Exchange(ref _downgrades, 0L),
            Interlocked.Exchange(ref _buyUpgrades, 0L),
            Interlocked.Exchange(ref _sellUpgrades, 0L));
}
