namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Shared deterministic, RNG-free numeric primitives used across the bot-simulation helpers, so the same
/// avalanche hash / bounded-walk step / power-law magnitude draw isn't re-derived per file. Pure and
/// unit-testable; callers that need byte-identical behaviour delegate to these (e.g.
/// <see cref="BotRegimeService"/>'s cohort hash and <see cref="BotSentimentService"/>'s RegimeDrift soft-wall).
/// </summary>
internal static class BotMath
{
    /// <summary>
    /// Stable per-key unit value in [0,1) — a single-input avalanche mix (same family as
    /// <see cref="StockProfileService"/>). Deterministic, call-order-independent, advances no RNG, so adjacent
    /// ids don't correlate. This is the canonical implementation the regime/cohort hashes delegate to.
    /// </summary>
    internal static double HashUnit01(int key)
    {
        unchecked
        {
            ulong h = (ulong)key * 0x9E3779B97F4A7C15UL + 0x165667B19E3779F9UL;
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
            return (double)(h & 0xFFFFFFFFUL) / 4294967296.0; // [0,1)
        }
    }

    /// <summary>
    /// Stable two-input unit value in [0,1). The two ints are packed into the high/low 32-bit lanes of a 64-bit
    /// word (via <c>(uint)</c> so sign is irrelevant) BEFORE the avalanche — NOT XOR-combined, which collides and
    /// would defeat any per-(a,b) reshuffle. Deterministic, RNG-free.
    /// </summary>
    internal static double HashUnit01(int a, int b)
    {
        unchecked
        {
            ulong packed = ((ulong)(uint)a << 32) | (uint)b;
            ulong h = packed * 0x9E3779B97F4A7C15UL + 0x165667B19E3779F9UL;
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
            return (double)(h & 0xFFFFFFFFUL) / 4294967296.0; // [0,1)
        }
    }

    /// <summary>
    /// §reaction-persistence: time-based EWMA / AR(1) KEEP weight (weight retained on the OLD value) for an
    /// elapsed <paramref name="dtSec"/> at a given <paramref name="halfLifeSec"/>: <c>0.5^(dt/halfLife)</c>.
    /// <c>dt ≤ 0</c> or <c>halfLife ≤ 0</c> ⇒ keep 1 (no update — first sight / clock skew / lever off). Pure ⇒
    /// unit-testable. Same shape as AiTradeService.TimeEwmaKeep, kept here so the pure bot-math
    /// helpers don't depend on the owning background service.
    /// </summary>
    internal static double HalfLifeKeep(double dtSec, double halfLifeSec)
        => (halfLifeSec <= 0.0 || dtSec <= 0.0) ? 1.0 : Math.Exp(-0.6931471805599453 * dtSec / halfLifeSec);

    /// <summary>
    /// One bounded-random-walk / accumulator step: add <paramref name="step"/>, apply a CUBIC soft-wall
    /// (≈0 near the middle so the value persists/trends, strong near ±cap so it can't escape), then hard-clamp
    /// to ±cap. <c>cap ≤ 0 ⇒ 0</c>. Pure ⇒ unit-testable. This is the shared shape RegimeDrift and the
    /// exogenous-shock accumulator both use.
    /// </summary>
    internal static double SoftWallStep(double prev, double step, double cap, double softWallK)
    {
        if (cap <= 0.0) return 0.0;
        double softPull = -softWallK * prev * (prev * prev) / (cap * cap); // = -k·prev³/cap²
        return Math.Clamp(prev + step + softPull, -cap, cap);
    }

    /// <summary>
    /// Power-law magnitude draw in [min, max]: <c>min + (max−min)·u^exp</c> with <c>u ~ U[0,1)</c>. An exponent
    /// &gt; 1 crowds the draw toward <paramref name="min"/> (many small, few large). Consumes exactly one RNG draw.
    /// </summary>
    internal static double DrawMagnitude(Random rng, double min, double max, double exp)
    {
        double span = max - min;
        return min + span * Math.Pow(rng.NextDouble(), exp);
    }
}
