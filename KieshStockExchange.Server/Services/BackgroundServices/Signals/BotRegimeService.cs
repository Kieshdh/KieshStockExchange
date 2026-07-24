using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §A2/A3/A4 shared market REGIME — a sharp, common directional factor a fraction of bots commit to
/// together so net order flow stops cancelling (Kirman herding). Unlike the smooth AR(1) sentiment, the
/// regime is a <b>2-state Markov sign</b> (+1 buy-lean / -1 sell-lean) that flips rarely and sharply, so
/// imbalance persists for a regime then reverses → trends and excursions rather than chop.
///
/// Sibling of <see cref="BotSentimentService"/>: one <see cref="Tick"/> per bot-loop iteration on the loop
/// thread (no locks), a seeded <c>_rng</c> (its OWN seed, independent of the per-bot decision RNGs), and a
/// <see cref="Reset"/>. The per-bot "follower" decision is a <b>pure hash</b> of the aiUserId — it consumes
/// no RNG on the hot path, so the regime adds O(1)/bot and O(1)/tick. Inert when disabled (the consuming
/// flags simply never read it); <see cref="Tick"/> is then a no-op so the regime RNG never advances either.
/// </summary>
internal sealed class BotRegimeService
{
    #region Configuration
    // Deterministic seed, distinct from sentiment(43)/fx(47)/fundamental(71).
    private const int RngSeed = 53;

    // Clamp the per-tick elapsed time so a stalled or first-after-reset loop can't distort the flip rate.
    private const double MinDtSec = BotMath.TickMinDtSec;
    private const double MaxDtSec = BotMath.TickMaxDtSec;
    #endregion

    #region State
    private readonly bool   _enabled;       // (herding || momentumDominance || roleSplit)
    private readonly double _regimeMeanSec; // mean regime duration → flipProb = 1 - exp(-dt/mean)

    private sbyte _sign = 1;                 // current regime direction
    private long  _ticksSinceFlip;
    private DateTime _lastTickUtc = DateTime.MaxValue; // inert until Reset arms the clock
    private DateTime _nextLogUtc  = DateTime.MaxValue;

    private Random _rng = new(RngSeed);
    private readonly ILogger<BotRegimeService> _logger;
    #endregion

    internal BotRegimeService(ILogger<BotRegimeService> logger,
        bool enabled = false, double regimeMeanSec = 960.0)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = enabled;
        _regimeMeanSec = Math.Max(1.0, regimeMeanSec);
    }

    #region Tick / Reset
    /// <summary>
    /// Advance the regime: with probability <c>1 - exp(-dt/mean)</c> flip the sign. Expressing the flip as a
    /// rate over the (clamped) elapsed time makes regime lengths independent of the loop frequency. One RNG
    /// draw per tick when enabled; a no-op (no draw) when disabled.
    /// </summary>
    internal void Tick(DateTime now)
    {
        if (!_enabled) return;
        if (_lastTickUtc == DateTime.MaxValue) return; // not reset yet
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastTickUtc = now;

        double flipProb = 1.0 - Math.Exp(-dt / _regimeMeanSec);
        if (_rng.NextDouble() < flipProb)
        {
            _sign = (sbyte)(-_sign);
            _ticksSinceFlip = 0;
        }
        else _ticksSinceFlip++;

        if (now >= _nextLogUtc)
        {
            _logger.LogInformation("Regime: sign={Sign} ticksSinceFlip={Ticks}", _sign, _ticksSinceFlip);
            _nextLogUtc = now + TimeSpan.FromSeconds(60);
        }
    }

    /// <summary>Open from a deterministic +1 regime and arm the tick clock.</summary>
    internal void Reset(DateTime now)
    {
        _rng = new Random(RngSeed);
        _sign = 1;
        _ticksSinceFlip = 0;
        _lastTickUtc = now;
        _nextLogUtc  = now + TimeSpan.FromSeconds(60);
    }
    #endregion

    #region Reads (hot path — pure, no RNG)
    /// <summary>Current regime direction as a signed unit (+1 buy-lean / -1 sell-lean).</summary>
    internal decimal RegimeSign => _sign;

    /// <summary>
    /// Whether this bot belongs to the directional follower cohort — a pure, stable avalanche hash of the
    /// aiUserId compared to the follower fraction <paramref name="f"/>. Deterministic and call-order-independent
    /// (advances no RNG), so the cohort is fixed for a given f and reproducible across runs.
    /// </summary>
    internal bool IsFollower(int aiUserId, decimal f)
    {
        if (f <= 0m) return false;
        if (f >= 1m) return true;
        return HashUnit(aiUserId) < f;
    }

    /// <summary>Directional buy-probability tilt for §A2 herding: <c>regimeSign·δ</c> for followers, else 0.</summary>
    internal decimal HerdTilt(int aiUserId, decimal f, decimal delta)
        => IsFollower(aiUserId, f) ? RegimeSign * delta : 0m;

    /// <summary>
    /// Stable per-bot unit value in [0,1) — the same pure avalanche hash that drives cohort selection,
    /// exposed for static callers (e.g. the Scalper Panic/Greed extreme-reaction split). Deterministic,
    /// call-order-independent, advances no RNG.
    /// </summary>
    internal static decimal StableUnit(int aiUserId) => HashUnit(aiUserId);

    // Pure hash → [0,1). Delegates to the shared BotMath avalanche (same math ⇒ byte-identical) so adjacent
    // ids don't correlate and the hash isn't re-derived per file.
    private static decimal HashUnit(int aiUserId) => (decimal)BotMath.HashUnit01(aiUserId);
    #endregion
}
