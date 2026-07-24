using System.Threading;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Process-static counter for engine DB commit round-trips, fed to the opt-in
/// <c>BotPhase</c> telemetry line so commits/sec and round-trips/order are
/// first-class. These are the only commit-bound numbers that transfer to prod
/// (per-tick ms and the scaler cap are docker commit-latency-skew artifacts), so
/// they are what a write-behind / group-commit soak must be judged by.
///
/// A "root commit" is one Postgres <c>COMMIT</c> on a fresh transaction — i.e. one
/// fsync round-trip. Nested <c>SAVEPOINT</c> releases are deliberately NOT counted
/// (they are not commit/fsync boundaries), so the count equals the number of
/// per-(stock,currency) group transactions plus phase-2 inserts, which is exactly
/// the quantity decision/commit decoupling aims to coalesce.
///
/// Counting is gated by <see cref="Enabled"/>, which the bot loop sets true ONLY
/// when <c>Bots:PhaseTimingSeconds &gt; 0</c> (the existing opt-in profiling switch).
/// When disabled (the default) every record call is a single bool check with no
/// side effect, so engine behavior is byte-identical.
/// </summary>
internal static class EngineCommitMetrics
{
    private static long _rootCommits;   // each root COMMIT == one fsync round-trip
    private static long _trades;        // settled Transaction rows this process
    private static int _activeCommitters;        // root commits currently inside their fsync-flush window
    private static int _maxConcurrentCommitters; // high-water mark of the above this process

    /// <summary>True only under the opt-in PhaseTiming diagnostic; default off.</summary>
    internal static bool Enabled;

    internal static void Configure(bool enabled) => Enabled = enabled;

    internal static void RecordRootCommit()
    {
        if (Enabled) Interlocked.Increment(ref _rootCommits);
    }

    internal static long ReadCommits() => Interlocked.Read(ref _rootCommits);

    // §A measurement gate: bracket the actual root COMMIT fsync window so the soak can observe HOW MANY
    // root commits overlap in their flush window. The default path already fans out per-(stock,currency)
    // group ~24-wide via Task.WhenAll, so Postgres may already amortize fsync across concurrent committers
    // — if this high-water mark is well above 1 under load, per-currency sharding (Workstream A) adds
    // little. Gated by Enabled ⇒ both calls are a single bool check (byte-identical) when the diagnostic
    // is off. CommitWindowExit must run in a finally so an aborted commit still decrements.
    internal static void CommitWindowEnter()
    {
        if (!Enabled) return;
        var n = Interlocked.Increment(ref _activeCommitters);
        int seen;
        while (n > (seen = Volatile.Read(ref _maxConcurrentCommitters)))
            if (Interlocked.CompareExchange(ref _maxConcurrentCommitters, n, seen) == seen) break;
    }

    internal static void CommitWindowExit()
    {
        if (Enabled) Interlocked.Decrement(ref _activeCommitters);
    }

    /// <summary>High-water mark of concurrent root committers observed this process (0 until first commit
    /// under the diagnostic). The Workstream-A "is the default already concurrent?" signal.</summary>
    internal static int ReadMaxConcurrentCommitters() => Volatile.Read(ref _maxConcurrentCommitters);

    // Settled trades this process, counted at the durable settle write. Fed to the
    // BotPhase line as trades/sec — the throughput signal a commit-cadence A/B needs,
    // since commits/sec falls BY DESIGN under coalescing and can't show whether the
    // bots admitted by a lighter tick are actually being served. Gated by Enabled, so
    // it's a single bool check (byte-identical) when the PhaseTiming diagnostic is off.
    internal static void RecordTrade(long n)
    {
        if (Enabled && n > 0) Interlocked.Add(ref _trades, n);
    }

    internal static long ReadTrades() => Interlocked.Read(ref _trades);
}
