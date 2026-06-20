using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §direct-flow chaser telemetry. Default OFF ⇒ zero cost (Enabled gate), draws no RNG and never affects
/// decision order or values, so byte-identical-off is preserved. When <c>Bots:ExogShock:ChaserProbe = true</c>
/// it counts, per window: how many real chase ORDERS were built, their NET signed and mean-absolute notional
/// (the discriminator between "chaser too weak" = small net, "not firing" = orders ≈ 0, and "one-sided drift"
/// = |net| ≈ total), and how many selected chases were SUPPRESSED (price 0 / cash- or share-clamped to 0) so a
/// flat probe can be told apart from a blocked one. Buys count +notional, sells −notional. Mirrors the
/// <see cref="ImpactHoldProbe"/> idiom (static, Enabled gate, Interlocked counters; notional accumulated in
/// whole currency units since Interlocked.Add has no decimal overload). <see cref="Drain"/> snapshots+resets.
/// </summary>
internal static class ChaserProbe
{
    internal static bool Enabled;
    private static long _orders;
    private static long _suppressed;
    private static long _netNotional;   // whole currency units, signed (+buy / −sell)
    private static long _absNotional;   // whole currency units, magnitude

    internal static void Configure(bool enabled) => Enabled = enabled;

    /// <summary>Record one real chase order built (placed into the batch). No-op when disabled.</summary>
    internal static void RecordOrder(bool isBuy, decimal notional)
    {
        if (!Enabled) return;
        long n = (long)System.Math.Round(notional);
        Interlocked.Increment(ref _orders);
        Interlocked.Add(ref _netNotional, isBuy ? n : -n);
        Interlocked.Add(ref _absNotional, n);
    }

    /// <summary>Record one selected chase that was suppressed before placement (price 0 / qty clamped to 0).</summary>
    internal static void RecordSuppressed()
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _suppressed);
    }

    /// <summary>Snapshot and reset; returns per-window orders, suppressed count, net signed notional, mean |notional|.</summary>
    internal static (long orders, long suppressed, double netNotional, double meanAbsNotional) Drain()
    {
        long o   = Interlocked.Exchange(ref _orders, 0L);
        long s   = Interlocked.Exchange(ref _suppressed, 0L);
        long net = Interlocked.Exchange(ref _netNotional, 0L);
        long abs = Interlocked.Exchange(ref _absNotional, 0L);
        return (o, s, net, o > 0L ? (double)abs / o : 0.0);
    }
}
