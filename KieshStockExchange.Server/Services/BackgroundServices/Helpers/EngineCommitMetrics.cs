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

    /// <summary>True only under the opt-in PhaseTiming diagnostic; default off.</summary>
    internal static bool Enabled;

    internal static void Configure(bool enabled) => Enabled = enabled;

    internal static void RecordRootCommit()
    {
        if (Enabled) Interlocked.Increment(ref _rootCommits);
    }

    internal static long ReadCommits() => Interlocked.Read(ref _rootCommits);
}
