using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Shared market-mood state as a mixture of continuous mean-reverting AR(1) processes.
/// Instead of encoding timeframe by reroll cadence (which froze the slow factors within a
/// session and let a constant bias run prices away), every score updates each tick and its
/// timescale is set purely by a persistence time-constant τ: α = exp(−Δt/τ). Fast scores
/// (small τ) flip sign every few seconds and bound the price; slow scores (large τ) drift
/// gently. Each score's steady-state amplitude is an independent weight σ (noise scaled by
/// σ·√(1−α²)), so τ and σ tune independently.
///
/// Per stock: a ring of fast→slow scores driving cross-stock dispersion. Global: a smaller,
/// slower ring shared by all stocks (common-mode regime). <see cref="GetSentiment"/> returns
/// the cached combined value per stock (per-stock ring + global ring + news shock), un-clamped
/// so callers can bias linearly inside ±1 and react to the overflow with market orders.
///
/// Driven by one <see cref="Tick"/> per bot-loop iteration (~1 Hz); Tick and GetSentiment both
/// run on the loop thread, so no locks are needed. Tick caches each stock's combined value, so
/// the per-bot GetSentiment hot path is a single dictionary read.
/// </summary>
internal sealed class BotSentimentService
{
    #region Ring configuration
    // Per-stock ring: fast→slow. τ in seconds, σ = steady-state amplitude (weight). Weight is
    // front-loaded on the fast scales so sentiment flips often (bounded) with gentle slow character.
    private static readonly double[] PerStockTauSec = { 20, 90, 360, 1800, 10800 };
    private static readonly double[] PerStockSigma  = { 0.25, 0.25, 0.20, 0.12, 0.08 };

    // Global ring: slower, common-mode, small weight (market regime, not dispersion).
    private static readonly double[] GlobalTauSec = { 600, 3600, 21600 };
    private static readonly double[] GlobalSigma  = { 0.10, 0.08, 0.06 };

    private static readonly int PerStockRings = PerStockTauSec.Length;
    private static readonly int GlobalRings   = GlobalTauSec.Length;

    // §slow-ring damp: rings at/above this τ are the SLOW sustained-bias scales (1800s/10800s) that
    // print the linear price drift. A config multiplier scales their amplitude to attack the drift at
    // its source, leaving the fast bounding scales untouched. Threshold is fixed; only the mult is config.
    private const double SlowRingTauThresholdSec = 1000.0;

    private const double Sqrt3 = 1.7320508075688772; // makes U(-1,1)*Sqrt3 unit-variance

    // Deterministic seed so the simulation is reproducible across runs.
    private const int RngSeed = 43;

    // Clamp the per-tick elapsed time so a stalled or first-after-reset loop can't distort α.
    private const double MinDtSec = BotMath.TickMinDtSec;
    private const double MaxDtSec = BotMath.TickMaxDtSec;

    // 50 stocks × 60s sampling × ~33 hours of runway. Each row is small.
    private const int RecentSamplesMax = 100_000;

    // News/earnings shocks: drop a decaying shock once it shrinks below this.
    private const double ShockFloor = 0.01;
    #endregion

    #region State
    private readonly Dictionary<int, double[]> _perStock = new(); // stockId → ring of scores
    private readonly double[] _global = new double[GlobalRings];
    private double _globalSum;

    // Per-stock transient news shock; non-zero only while an event decays.
    private readonly Dictionary<int, double> _shock = new();

    // Combined per-stock value (per-stock ring + global + shock), refreshed each tick.
    private readonly Dictionary<int, decimal> _combined = new();

    private DateTime _lastTickUtc = DateTime.MinValue;
    private DateTime _nextLogUtc   = DateTime.MaxValue;

    private Random _rng = new(RngSeed);

    private readonly Queue<SentimentSample> _samples = new();
    private readonly RingBufferStore<SentimentSample> _store;

    private readonly bool _newsEvents;
    private readonly double _shockMinMagnitude;
    private readonly double _shockMaxMagnitude;
    private readonly double _shockMagnitudeExponent; // >1 skews events toward the small end
    private readonly double _shockDecayPerTick;
    private readonly double _shockArrivalProbPerTick; // per stock per tick

    // Sentiment-dynamics §: two-timescale EWMA of the combined-sentiment slope ds = d(s)/dt. A fast slope
    // (τ_fast) for the twitchy Scalper and a slow one (τ_slow) for the patient strategies. Computed inside
    // Tick on the loop thread, with NO RNG; gated by _slopeEnabled so the off path is untouched and free.
    private readonly bool   _slopeEnabled;
    private readonly double _slopeTauFastSec;
    private readonly double _slopeTauSlowSec;
    private readonly Dictionary<int, double> _slopePrev = new(); // stockId → previous combined value (double)
    private readonly Dictionary<int, double> _dsFast    = new(); // stockId → fast EWMA slope
    private readonly Dictionary<int, double> _dsSlow    = new(); // stockId → slow EWMA slope

    // Realism §price-reaction (#2): contrarian feedback so a SUSTAINED price move pushes sentiment the
    // OTHER way (breaks the linear drift a long-lived slow ring otherwise prints). Leaky-integrates the
    // per-tick return over τ — that sum ≈ the fractional move over the window and is tick-rate-stable —
    // dead-bands small moves (so tick noise / the 1-min scale is left alone), and adds a clamped
    // contrarian term to combined. Additive + RNG-free ⇒ flag-off is byte-identical AND flag-on leaves
    // the OU rings' sequence untouched (the reaction rides on top).
    private readonly bool   _priceReaction;
    private readonly double _reactStrength;
    private readonly double _reactTauSec;
    private readonly double _reactDeadband;
    private readonly double _reactCap;
    private readonly Func<int, double>? _recentReturn;
    // §impact-decouple A: when non-null (flag on), the price-reaction term reads the >1-min-decoupled return
    // instead of _recentReturn's ~1s return. Null ⇒ uses _recentReturn ⇒ byte-identical. Leaves the activity
    // driver (_recentReturn = RecentReturnForActivity) untouched.
    private readonly Func<int, double>? _reactionReturn;
    private readonly Dictionary<int, double> _cumRet = new(); // leaky-integrated recent return per stock

    // #3 waves: a FAST positive-feedback (momentum) term that composes with the slow contrarian above —
    // price up → brief sentiment UP (FOMO chase) over a short τ, then #2's slow contrarian sours it →
    // boom-bust waves instead of linear drift. Default momStrength=0 ⇒ #3 off (even when #2 is on).
    private readonly double _momStrength;
    private readonly double _momTauSec;
    private readonly double _momCap;
    private readonly Dictionary<int, double> _cumRetFast = new();

    // §slow-ring damp: PerStockSigma with SlowRingDamp folded into the slow rings (×1.0 ⇒ identical doubles).
    private readonly double[] _perStockSigmaEff;
    // §correlation lever: GlobalSigma × GlobalSigmaMult (per-stock mult folds into _perStockSigmaEff above). 1.0 ⇒ identical.
    private readonly double[] _globalSigmaEff;

    // §System A — RegimeDrift: a PERSISTENT common-mode bounded random walk per stock (NOT mean-reverting like
    // the OU rings) so price can TREND/wander for minutes. Bounded by a cubic soft-wall (free in the middle,
    // walled near ±Cap) so it can't run away. Dedicated RNG drawn ONLY when enabled ⇒ off path byte-identical.
    private readonly bool   _regimeEnabled;
    private readonly double _regimeStepSigma;
    private readonly double _regimeCap;
    private readonly double _regimeSoftWallK;
    private readonly double _regimeStrength;
    private readonly Dictionary<int, double> _regime = new();
    private Random _regimeRng = new(RngSeed ^ 0x2A2A);

    // §co-movement: a SHARED (single) bounded random walk — the "market factor" — that EVERY stock loads
    // onto via a deterministic per-stock beta, so the cohort CO-MOVES (positive cross-stock return
    // correlation, which is ~0 today = 50 independent universes). Sibling of RegimeDrift: same cubic
    // soft-wall, but ONE walk shared across all stocks (not per-stock independent), scaled per stock by
    // beta. Dedicated RNG drawn ONLY when enabled ⇒ off path byte-identical. Beta is a pure hash of
    // stockId (no reseed) cached on first use, ~1.0 ± spread, clamped positive.
    private readonly bool   _coMoveEnabled;
    private readonly double _coMoveStepSigma;
    private readonly double _coMoveCap;
    private readonly double _coMoveSoftWallK;
    private readonly double _coMoveStrength;
    private readonly double _coMoveBetaSpread;
    private double _coMoveFactor;
    private Random _coMoveRng = new(RngSeed ^ 0x5C5C);
    private readonly Dictionary<int, double> _coMoveBeta = new();

    // §global-shock: a single MARKET-WIDE decaying shock — the "elevator down + correlation" driver. ONE Poisson
    // stream for the whole market; on arrival gets a signed magnitude (DownBias ⇒ mostly bearish "crashes", rare
    // up melt-ups) added to a scalar that decays like the per-stock shocks and is folded into EVERY stock's combined
    // sentiment ⇒ all stocks move TOGETHER (cross-stock correlation) + turn the fleet bearish at once (⇒ fleet-wide
    // bear-short = the down-move that a single aggressor can't achieve, since the book absorbs one source). Dedicated
    // RNG drawn ONLY when enabled ⇒ off leaves the value 0 AND every other RNG sequence untouched (byte-identical).
    private readonly bool   _globalShockEnabled;
    private readonly double _globalShockMinMagnitude;
    private readonly double _globalShockMaxMagnitude;
    private readonly double _globalShockMagnitudeExponent;
    private readonly double _globalShockDecayPerTick;
    private readonly double _globalShockArrivalProbPerTick; // whole-market, per tick
    private readonly double _globalShockDownBias;           // P(event is bearish); ~0.85 = correlated fear
    private double _globalShock;
    private Random _globalShockRng = new(RngSeed ^ 0x6B6B);
    #endregion

    #region Services and Constructor
    private readonly IStockService _stocks;
    private readonly StockProfileService _profiles;
    private readonly ILogger<BotSentimentService> _logger;

    // §P6 liveliness: per-stock sentiment amplitude multiplier (calm names quieter, meme names louder).
    private double AmpMult(int stockId) => (double)_profiles.Get(stockId).SentimentAmplitudeMult;

    internal BotSentimentService(IStockService stocks, StockProfileService profiles,
        ILogger<BotSentimentService> logger,
        bool newsEvents = true, double shockMeanIntervalHours = 6.0,
        decimal shockMinMagnitude = 0.3m, decimal shockMaxMagnitude = 1.5m,
        double shockMagnitudeExponent = 3.0, decimal shockDecayPerTick = 0.999m,
        bool slopeEnabled = false, double slopeTauFastSec = 45.0, double slopeTauSlowSec = 180.0,
        Func<int, double>? recentReturn = null,
        bool priceReaction = false, double reactStrength = 6.0, double reactTauSec = 300.0,
        double reactDeadband = 0.01, double reactCap = 0.40,
        double momStrength = 0.0, double momTauSec = 60.0, double momCap = 0.25,
        double slowRingDamp = 1.0,
        bool regimeEnabled = false, double regimeStepSigma = 0.03, double regimeCap = 0.5,
        double regimeSoftWallK = 0.1, double regimeStrength = 1.0,
        // §co-movement: shared market-factor walk + per-stock beta dispersion. Default off ⇒ byte-identical.
        bool coMoveEnabled = false, double coMoveStepSigma = 0.03, double coMoveCap = 0.4,
        double coMoveSoftWallK = 0.1, double coMoveStrength = 0.5, double coMoveBetaSpread = 0.4,
        // §impact-decouple A: optional >1-min-decoupled return for the price-reaction term. Null ⇒ byte-identical.
        Func<int, double>? reactionReturn = null,
        // §global-shock: market-wide bearish sentiment event (elevator-down + cross-stock correlation). Off ⇒ byte-identical.
        bool globalShockEnabled = false, double globalShockMeanIntervalHours = 3.0,
        decimal globalShockMinMagnitude = 0.3m, decimal globalShockMaxMagnitude = 1.5m,
        double globalShockMagnitudeExponent = 3.0, decimal globalShockDecayPerTick = 0.999m,
        double globalShockDownBias = 0.85,
        // §sentiment-ring amplitude multipliers = the cross-stock CORRELATION lever. Lower per-stock + raise global
        // ⇒ the shared common-mode dominates the idiosyncratic ⇒ higher cross-stock corr. 1.0/1.0 ⇒ byte-identical.
        double perStockSigmaMult = 1.0, double globalSigmaMult = 1.0)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _newsEvents = newsEvents;
        _shockMinMagnitude = (double)shockMinMagnitude;
        _shockMaxMagnitude = Math.Max((double)shockMinMagnitude, (double)shockMaxMagnitude);
        _shockMagnitudeExponent = Math.Max(1.0, shockMagnitudeExponent);
        _shockDecayPerTick = (double)shockDecayPerTick;
        _shockArrivalProbPerTick = 1.0 / (Math.Max(0.0001, shockMeanIntervalHours) * 3600.0);
        _slopeEnabled    = slopeEnabled;
        _slopeTauFastSec = Math.Max(MinDtSec, slopeTauFastSec);
        _slopeTauSlowSec = Math.Max(_slopeTauFastSec, slopeTauSlowSec);
        _recentReturn   = recentReturn;
        _reactionReturn = reactionReturn;
        _priceReaction  = priceReaction;
        _reactStrength  = Math.Max(0.0, reactStrength);
        _reactTauSec    = Math.Max(MinDtSec, reactTauSec);
        _reactDeadband  = Math.Max(0.0, reactDeadband);
        _reactCap       = Math.Max(0.0, reactCap);
        _momStrength    = Math.Max(0.0, momStrength);
        _momTauSec      = Math.Max(MinDtSec, momTauSec);
        _momCap         = Math.Max(0.0, momCap);

        // Fold SlowRingDamp AND the correlation-lever σ multipliers into the ring amplitudes once; 1.0s ⇒ identical.
        double slowDamp   = Math.Max(0.0, slowRingDamp);
        double perStkMult = Math.Max(0.0, perStockSigmaMult);
        double glbMult    = Math.Max(0.0, globalSigmaMult);
        _perStockSigmaEff = new double[PerStockRings];
        for (int k = 0; k < PerStockRings; k++)
            _perStockSigmaEff[k] = EffectivePerStockSigma(k, slowDamp, perStkMult);
        _globalSigmaEff = new double[GlobalRings];
        for (int k = 0; k < GlobalRings; k++)
            _globalSigmaEff[k] = EffectiveGlobalSigma(k, glbMult);

        _regimeEnabled   = regimeEnabled;
        _regimeStepSigma = Math.Max(0.0, regimeStepSigma);
        _regimeCap       = Math.Max(0.0, regimeCap);
        _regimeSoftWallK = Math.Max(0.0, regimeSoftWallK);
        _regimeStrength  = Math.Max(0.0, regimeStrength);

        _coMoveEnabled    = coMoveEnabled;
        _coMoveStepSigma  = Math.Max(0.0, coMoveStepSigma);
        _coMoveCap        = Math.Max(0.0, coMoveCap);
        _coMoveSoftWallK  = Math.Max(0.0, coMoveSoftWallK);
        _coMoveStrength   = Math.Max(0.0, coMoveStrength);
        _coMoveBetaSpread = Math.Max(0.0, coMoveBetaSpread);

        _globalShockEnabled            = globalShockEnabled;
        _globalShockMinMagnitude       = (double)globalShockMinMagnitude;
        _globalShockMaxMagnitude       = Math.Max((double)globalShockMinMagnitude, (double)globalShockMaxMagnitude);
        _globalShockMagnitudeExponent  = Math.Max(1.0, globalShockMagnitudeExponent);
        _globalShockDecayPerTick       = (double)globalShockDecayPerTick;
        _globalShockArrivalProbPerTick = 1.0 / (Math.Max(0.0001, globalShockMeanIntervalHours) * 3600.0);
        _globalShockDownBias           = Math.Clamp(globalShockDownBias, 0.0, 1.0);

        _store = new RingBufferStore<SentimentSample>("data/telemetry/bot_sentiment.ndjson");

        var prior = _store.LoadTail(RecentSamplesMax);
        foreach (var s in prior) _samples.Enqueue(s);
        if (prior.Count > 0)
            _logger.LogInformation("BotSentimentService: replayed {Count} sample(s) from disk.", prior.Count);

        // Inert until Reset(now); AiTradeService calls Reset before the bot loop starts.
        _lastTickUtc = DateTime.MaxValue;
    }
    #endregion

    #region Tick
    /// <summary>
    /// Advance every score by the elapsed time, decay/arrive news shocks, and refresh the
    /// per-stock combined cache. Called once per bot-loop iteration.
    /// </summary>
    internal void Tick(DateTime now)
    {
        if (_lastTickUtc == DateTime.MaxValue) return; // not reset yet
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastTickUtc = now;

        // Per-tick α and noise scale for each ring (α = exp(−Δt/τ); steady-state std == σ).
        Span<double> gAlpha = stackalloc double[GlobalRings];
        Span<double> gNoise = stackalloc double[GlobalRings];
        for (int k = 0; k < GlobalRings; k++)
        {
            double a = Math.Exp(-dt / GlobalTauSec[k]);
            gAlpha[k] = a;
            gNoise[k] = _globalSigmaEff[k] * Math.Sqrt(1.0 - a * a);
        }
        Span<double> sAlpha = stackalloc double[PerStockRings];
        Span<double> sNoise = stackalloc double[PerStockRings];
        for (int k = 0; k < PerStockRings; k++)
        {
            double a = Math.Exp(-dt / PerStockTauSec[k]);
            sAlpha[k] = a;
            sNoise[k] = _perStockSigmaEff[k] * Math.Sqrt(1.0 - a * a);
        }

        // Global ring (computed once; common-mode across all stocks).
        _globalSum = 0.0;
        for (int k = 0; k < GlobalRings; k++)
        {
            _global[k] = gAlpha[k] * _global[k] + gNoise[k] * UnitNoise();
            _globalSum += _global[k];
        }

        // §co-movement: advance the single SHARED market-factor walk once per tick (per-stock loading
        // applied in the combine loop below). Same cubic soft-wall as RegimeDrift; dedicated RNG, drawn
        // ONLY when enabled ⇒ flag-off leaves the value AND every other RNG sequence untouched.
        if (_coMoveEnabled && _coMoveCap > 0.0)
        {
            double cstep = (_coMoveRng.NextDouble() * 2.0 - 1.0) * Sqrt3 * _coMoveStepSigma * Math.Sqrt(dt);
            _coMoveFactor = RegimeStep(_coMoveFactor, cstep, _coMoveCap, _coMoveSoftWallK);
        }

        if (_newsEvents) StepShocks();
        // §global-shock: advance the single market-wide shock (decay + one whole-market arrival). Gated ⇒ off
        // draws no RNG here and leaves _globalShock at 0 (the combine below then adds an exact 0.0 = byte-identical).
        if (_globalShockEnabled) StepGlobalShock();

        // Per-stock rings + combined cache.
        foreach (var sid in _stocks.ById.Keys)
        {
            if (!_perStock.TryGetValue(sid, out var ring)) { ring = new double[PerStockRings]; _perStock[sid] = ring; }
            double amp = AmpMult(sid);
            // §global-shock rides alongside the global OU ring as a second common-mode term ⇒ every stock gets the
            // SAME shift (cross-stock correlation). 0 when off ⇒ exact byte-identical (adding 0.0 is exact in IEEE754).
            double sum = _globalSum + _globalShock;
            for (int k = 0; k < PerStockRings; k++)
            {
                ring[k] = sAlpha[k] * ring[k] + sNoise[k] * amp * UnitNoise();
                sum += ring[k];
            }
            if (_shock.TryGetValue(sid, out var sh)) sum += sh;

            // §price-reaction (#2): contrarian push from the stock's own sustained move. Leaky-integrate
            // the per-tick return over τ (≈ fractional move over the window, tick-rate-stable), dead-band
            // small moves, and add a clamped OPPOSITE-sign term so a multi-minute up-drift bends back down.
            if (_priceReaction && _recentReturn != null)
            {
                // §impact-decouple A: use the >1-min-decoupled return when wired (flag on), else the legacy ~1s
                // return. Selector is byte-identical off because _reactionReturn is null unless the flag is on.
                double r = _reactionReturn != null ? _reactionReturn(sid) : _recentReturn(sid);
                // #2 slow contrarian: leaky-integrate over τ, dead-band, push OPPOSITE the sustained move.
                double cum = Math.Exp(-dt / _reactTauSec) * _cumRet.GetValueOrDefault(sid) + r;
                _cumRet[sid] = cum;
                sum += Math.Clamp(-_reactStrength * Deadband(cum, _reactDeadband), -_reactCap, _reactCap);
                // #3 fast momentum (default off): leaky-integrate over a SHORT τ, push SAME direction (brief
                // FOMO chase) so the slow contrarian above turns a drift into a boom-bust wave.
                if (_momStrength > 0.0)
                {
                    double cf = Math.Exp(-dt / _momTauSec) * _cumRetFast.GetValueOrDefault(sid) + r;
                    _cumRetFast[sid] = cf;
                    sum += Math.Clamp(_momStrength * cf, -_momCap, _momCap);
                }
            }

            // §System A RegimeDrift: advance the per-stock bounded random walk and add its persistent,
            // common-mode push. Increment std = StepSigma·√dt (unit-variance draw ×Sqrt3). Dedicated RNG,
            // drawn ONLY when enabled ⇒ flag-off leaves both the value AND the main RNG sequence untouched.
            if (_regimeEnabled && _regimeCap > 0.0)
            {
                double step = (_regimeRng.NextDouble() * 2.0 - 1.0) * Sqrt3 * _regimeStepSigma * Math.Sqrt(dt);
                double rg = RegimeStep(_regime.GetValueOrDefault(sid), step, _regimeCap, _regimeSoftWallK);
                _regime[sid] = rg;
                sum += _regimeStrength * rg;
            }

            _combined[sid] = (decimal)sum;

            // Sentiment-dynamics §: two-timescale EWMA of the slope (sign = trend direction, magnitude =
            // conviction). No RNG; loop-thread only; skipped entirely when disabled. raw uses the combined
            // value BEFORE this tick (_slopePrev), seeded to sum on the first observation so ds opens at 0.
            if (_slopeEnabled)
            {
                double sPrev = _slopePrev.TryGetValue(sid, out var pv) ? pv : sum;
                _dsFast[sid] = EwmaSlope(_dsFast.GetValueOrDefault(sid), sum, sPrev, dt, _slopeTauFastSec);
                _dsSlow[sid] = EwmaSlope(_dsSlow.GetValueOrDefault(sid), sum, sPrev, dt, _slopeTauSlowSec);
                _slopePrev[sid] = sum;
            }
        }

        if (now >= _nextLogUtc) { LogCombinedSentiment(now); _nextLogUtc = now + TimeSpan.FromSeconds(60); }
    }

    /// <summary>
    /// One EWMA step of the per-stock sentiment slope. raw = (sNow − sPrev)/dt; the smoothing keeps a
    /// fraction exp(−dt/τ) of the prior estimate (so the horizon is τ seconds, frame-rate independent) and
    /// blends in (1 − that) of the fresh raw slope. Pure &amp; RNG-free → unit-testable.
    /// </summary>
    internal static double EwmaSlope(double dsPrev, double sNow, double sPrev, double dt, double tauSec)
    {
        if (dt <= 0.0) return dsPrev;
        double raw  = (sNow - sPrev) / dt;
        double keep = Math.Exp(-dt / Math.Max(MinDtSec, tauSec));
        return keep * dsPrev + (1.0 - keep) * raw;
    }

    // §slow-ring damp: a SLOW ring (τ ≥ threshold) gets its amplitude scaled by damp; fast rings pass
    // through unchanged. ×1.0 ⇒ identical double (off path byte-identical). Pure ⇒ unit-testable.
    internal static double SlowRingSigma(double baseSigma, double tauSec, double damp)
        => baseSigma * (tauSec >= SlowRingTauThresholdSec ? damp : 1.0);

    // §correlation lever: effective ring σ = base (× SlowRingDamp for slow per-stock rings) × the config multiplier.
    // Pure ⇒ unit-testable; mult 1.0 ⇒ identical double (byte-identical off path).
    internal static double EffectivePerStockSigma(int ring, double slowDamp, double mult)
        => SlowRingSigma(PerStockSigma[ring], PerStockTauSec[ring], slowDamp) * mult;
    internal static double EffectiveGlobalSigma(int ring, double mult) => GlobalSigma[ring] * mult;
    internal static int PerStockRingCount => PerStockRings;
    internal static int GlobalRingCount   => GlobalRings;

    // §System A RegimeDrift: one bounded-random-walk step — add the increment, apply a CUBIC soft-wall
    // (≈0 near the middle so the walk persists/trends, strong near ±cap so it can't escape), then hard-clamp.
    // Pure ⇒ unit-testable.
    internal static double RegimeStep(double prev, double step, double cap, double softWallK)
        => BotMath.SoftWallStep(prev, step, cap, softWallK); // shared cubic soft-wall (same math ⇒ byte-identical)

    // §co-movement: deterministic per-stock loading (beta) on the shared market factor — a stable hash of
    // stockId mapped to ~1.0 ± spread, clamped positive so co-movement stays positive with realistic beta
    // dispersion (a few low-beta names, most near 1, a few high). Pure (no RNG, no reseed); cached on first use.
    private double BetaOf(int stockId)
    {
        if (_coMoveBeta.TryGetValue(stockId, out var b)) return b;
        b = CoMoveBeta(stockId, _coMoveBetaSpread);
        _coMoveBeta[stockId] = b;
        return b;
    }

    // §co-movement: pure beta computation (extracted for unit tests) — a stable stockId hash mapped to
    // 1.0 ± spread and clamped positive. No RNG, no reseed ⇒ deterministic, call-order-independent, and
    // runtime-only (a reseed isn't required to change the dispersion). spread 0 ⇒ exactly 1.0 for every stock.
    internal static double CoMoveBeta(int stockId, double betaSpread)
        => Math.Max(0.05, 1.0 + betaSpread * (2.0 * BotMath.HashUnit01(stockId) - 1.0));

    // §co-movement: the shared market-factor FRACTIONAL shift for a stock = Strength × beta × factor.
    // Consumed by FundamentalService to co-move the per-stock ANCHOR TARGETS together (the channel the
    // value-anchor SUPPORTS — a market-wide repricing — vs the sentiment tilt it DAMPS). 0 when disabled
    // ⇒ FundamentalService's composition is byte-identical off. (The bounded walk + beta still live here.)
    internal double CoMoveShift(int stockId)
        => _coMoveEnabled ? _coMoveStrength * BetaOf(stockId) * _coMoveFactor : 0.0;

    // §price-reaction (#2): signed dead-band — zero within ±band, pass only the excess beyond it.
    internal static double Deadband(double x, double band)
    {
        if (band <= 0.0) return x;
        double m = Math.Abs(x) - band;
        return m <= 0.0 ? 0.0 : (x < 0.0 ? -m : m);
    }

    // Unit-variance draw: U(-1,1)*√3.
    private double UnitNoise() => (_rng.NextDouble() * 2.0 - 1.0) * Sqrt3;

    /// <summary>
    /// Decay active news shocks, then roll a low-rate Poisson arrival per stock. A fired shock
    /// jumps a stock's sentiment past ±1 (sign random) and fades over minutes. Advances the RNG
    /// only when news events are enabled, so the disabled path leaves the sequence unchanged.
    /// </summary>
    private void StepShocks()
    {
        if (_shock.Count > 0)
        {
            foreach (var sid in _shock.Keys.ToList())
            {
                var v = _shock[sid] * _shockDecayPerTick;
                if (Math.Abs(v) < ShockFloor) _shock.Remove(sid);
                else _shock[sid] = v;
            }
        }

        foreach (var sid in _stocks.ById.Keys)
        {
            if (_rng.NextDouble() >= _shockArrivalProbPerTick) continue;
            var sign = _rng.NextDouble() < 0.5 ? -1.0 : 1.0;
            // U^exp (exp>1) crowds the draw near the floor: many small events, few big.
            var span = _shockMaxMagnitude - _shockMinMagnitude;
            var mag = _shockMinMagnitude + span * Math.Pow(_rng.NextDouble(), _shockMagnitudeExponent);
            var delta = sign * mag;
            _shock.TryGetValue(sid, out var cur);
            _shock[sid] = cur + delta;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var sym = _stocks.TryGetSymbol(sid, out var s) ? s : sid.ToString();
                _logger.LogInformation("News shock: {Symbol} {Delta:+0.00;-0.00}", sym, delta);
            }
        }
    }

    /// <summary>
    /// §global-shock: decay the single market-wide shock, then roll ONE whole-market Poisson arrival. On fire a
    /// signed magnitude (DownBias ⇒ mostly bearish) is added to the scalar Tick folds into EVERY stock ⇒ a correlated
    /// market-wide move that turns the whole fleet bearish at once. Uses a DEDICATED RNG so the per-stock ring/shock
    /// sequences stay untouched. Called only when enabled ⇒ the off path draws nothing here and leaves _globalShock 0.
    /// </summary>
    private void StepGlobalShock()
    {
        _globalShock = Math.Abs(_globalShock) < ShockFloor ? 0.0 : _globalShock * _globalShockDecayPerTick;

        if (_globalShockRng.NextDouble() >= _globalShockArrivalProbPerTick) return;
        var delta = GlobalShockDelta(_globalShockRng.NextDouble(), _globalShockRng.NextDouble(),
            _globalShockMinMagnitude, _globalShockMaxMagnitude, _globalShockMagnitudeExponent, _globalShockDownBias);
        _globalShock += delta;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("GLOBAL shock: {Delta:+0.00;-0.00} (accum {Accum:+0.00;-0.00})", delta, _globalShock);
    }

    // §global-shock: pure signed-magnitude draw — sign from DownBias (signUniform &lt; downBias ⇒ bearish −),
    // magnitude min + span·U^exp (exp&gt;1 crowds toward the floor: many small, few big), same shape as the per-stock
    // shock. Extracted so the sign/magnitude logic is unit-testable. Pure ⇒ deterministic, no RNG, no state.
    internal static double GlobalShockDelta(double signUniform, double magUniform,
        double min, double max, double exp, double downBias)
    {
        double sign = signUniform < downBias ? -1.0 : 1.0;
        double span = Math.Max(0.0, max - min);
        double mag  = min + span * Math.Pow(Math.Clamp(magUniform, 0.0, 1.0), Math.Max(1.0, exp));
        return sign * mag;
    }

    /// <summary>
    /// Combined sentiment for <paramref name="stockId"/> (per-stock ring + global ring + news
    /// shock), read from the cache Tick maintains. Un-clamped: typically ±1, more during a shock.
    /// </summary>
    internal decimal GetSentiment(int stockId)
        => _combined.TryGetValue(stockId, out var v) ? v : 0m;

    /// <summary>
    /// The RAW shared common-mode signal this tick = global OU ring sum + global shock, identical for EVERY stock
    /// (unlike <see cref="GetSentiment"/>, which is the per-stock blend). Sign = shared market direction, magnitude =
    /// conviction (~±0.2-0.4, more during a global shock). Exposed so the trend-follower can chase it as a fleet-wide
    /// TAKER → cross-stock correlation (a shared buyProb tilt is book-absorbed; shared taker flow is not). Loop-thread read.
    /// </summary>
    internal decimal GlobalSignal() => (decimal)(_globalSum + _globalShock);

    /// <summary>
    /// Sentiment-dynamics §: the EWMA slope ds = d(sentiment)/dt for a stock — fast timescale when
    /// <paramref name="fast"/> is true, slow otherwise. 0 when the feature is disabled or the stock is
    /// unseen. Loop-thread read, like <see cref="GetSentiment"/>; sign = trend direction, magnitude = conviction.
    /// </summary>
    internal decimal GetSentimentSlope(int stockId, bool fast)
        => (decimal)(fast ? _dsFast : _dsSlow).GetValueOrDefault(stockId);

    /// <summary>
    /// Magnitude of the currently-decaying news shock for a stock (0 when none), exposed so the activity
    /// field (Pillar B) can use news arrivals as a Hawkes excitation driver. Loop-thread read, like
    /// <see cref="GetSentiment"/>.
    /// </summary>
    internal double ShockMagnitude(int stockId)
        => _shock.TryGetValue(stockId, out var v) ? Math.Abs(v) : 0.0;
    #endregion

    #region Logging
    /// <summary>
    /// One combined snapshot per minute: the global mood, then per-stock COMBINED sentiment
    /// (what bots act on), 10 stocks per line. Per-bot personal sentiment is added downstream.
    /// </summary>
    private void LogCombinedSentiment(DateTime now)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        const int PerLine = 10;
        const string NumFmt = ":+0.00;-0.00;0.00";

        var tpl = new StringBuilder(1024);
        var args = new List<object>(128);
        void Num(decimal v) { tpl.Append('{').Append(args.Count).Append(NumFmt).Append('}'); args.Add(v); }

        tpl.Append("Sentiment @ {").Append(args.Count).Append('}');
        args.Add(now.ToLocalTime().ToString("HH:mm:ss"));
        tpl.Append(" Global="); Num((decimal)_globalSum);
        if (_coMoveEnabled) { tpl.Append(" Mkt="); Num((decimal)_coMoveFactor); }
        tpl.Append(" |\n");

        int onThisLine = 0;
        foreach (var sid in _stocks.ById.Keys)
        {
            if (onThisLine == PerLine) { tpl.Append('\n'); onThisLine = 0; }
            var symbol = _stocks.TryGetSymbol(sid, out var s) ? s : sid.ToString();
            tpl.Append(' ').Append(symbol).Append(':');
            Num(GetSentiment(sid));
            onThisLine++;
        }

        // Sentiment-dynamics §: append a slow-slope magnitude summary so SlopeScaleSlow can be calibrated
        // (tune σ so a typical |ds| lands tanh(|ds|/σ) in ~0.3–0.8). Only when the feature is on.
        if (_slopeEnabled && _dsSlow.Count > 0)
        {
            double maxAbs = 0.0, sumAbs = 0.0;
            foreach (var v in _dsSlow.Values) { var a = Math.Abs(v); sumAbs += a; if (a > maxAbs) maxAbs = a; }
            tpl.Append("\n  dsSlow: max|{").Append(args.Count).Append(":0.00000}|");
            args.Add((decimal)maxAbs);
            tpl.Append(" mean|{").Append(args.Count).Append(":0.00000}|");
            args.Add((decimal)(sumAbs / _dsSlow.Count));
        }

        _logger.LogInformation(tpl.ToString(), args.ToArray());
    }
    #endregion

    #region Reset
    /// <summary>
    /// Open with a fully NEUTRAL shared sentiment — every global and per-stock ring at 0 — and arm
    /// the tick clock from <paramref name="now"/>. Any bias at t=0 (market-wide global OR per-name)
    /// shoves price before the opening book has the depth to absorb it, causing early extreme moves;
    /// the fixed RngSeed also froze that bias the same (net-negative) way every run. The rings walk
    /// up from 0 via the AR step in Tick() — the fast 20s/90s scales rebuild dispersion within a
    /// minute — and per-BOT personal sentiment still gives immediate variety so bots don't act in
    /// lockstep while the chart fills.
    /// </summary>
    internal void Reset(DateTime now)
    {
        _rng = new Random(RngSeed);
        _perStock.Clear();
        _shock.Clear();
        _combined.Clear();
        _slopePrev.Clear();
        _dsFast.Clear();
        _dsSlow.Clear();
        _cumRet.Clear();
        _cumRetFast.Clear();
        _regime.Clear();
        _regimeRng = new Random(RngSeed ^ 0x2A2A);
        _coMoveFactor = 0.0;
        _coMoveRng = new Random(RngSeed ^ 0x5C5C);
        _coMoveBeta.Clear();
        lock (_samples) _samples.Clear();

        _globalSum = 0.0;
        for (int k = 0; k < GlobalRings; k++) _global[k] = 0.0; // neutral global open

        foreach (var sid in _stocks.ById.Keys)
        {
            _perStock[sid] = new double[PerStockRings]; // zeros — neutral per-name open
            _combined[sid] = 0m;
        }

        _lastTickUtc = now;
        _nextLogUtc  = now + TimeSpan.FromSeconds(60);
        _logger.LogDebug("BotSentimentService reset: {Stocks} stocks × {Rings} per-stock rings + {G} global.",
            _stocks.ById.Count, PerStockRings, GlobalRings);
    }
    #endregion

    #region Snapshot / Export
    /// <summary>
    /// Append one row per known stock to the export ring: combined value, global component, and
    /// any active news shock. Drives the sentiment CSV the Bot Dashboard exports.
    /// </summary>
    internal void LogSnapshot()
    {
        var now = TimeHelper.NowUtc();
        lock (_samples)
        {
            foreach (var sid in _stocks.ById.Keys)
            {
                _shock.TryGetValue(sid, out var sh);
                _combined.TryGetValue(sid, out var combined);
                var sample = new SentimentSample(
                    TimestampUtc: now,
                    StockId:      sid,
                    Combined:     combined,
                    GlobalSum:    (decimal)_globalSum,
                    Shock:        (decimal)sh);
                _samples.Enqueue(sample);
                _store.Append(sample);
            }
            while (_samples.Count > RecentSamplesMax) _samples.Dequeue();
        }
    }

    internal int SampleCount { get { lock (_samples) return _samples.Count; } }

    internal string SuggestedExportFileName => $"bot_sentiment_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    internal string BuildCsv(CancellationToken ct = default)
    {
        SentimentSample[] snapshot;
        lock (_samples) snapshot = _samples.ToArray();

        var sb = new StringBuilder(512 + snapshot.Length * 64);
        sb.AppendLine("TimestampUtc,StockId,Combined,GlobalSum,Shock");
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = snapshot[i];
            sb.Append(r.TimestampUtc.ToString("O", inv)).Append(',')
              .Append(r.StockId).Append(',')
              .Append(r.Combined.ToString(inv)).Append(',')
              .Append(r.GlobalSum.ToString(inv)).Append(',')
              .Append(r.Shock.ToString(inv))
              .Append('\n');
        }
        return sb.ToString();
    }

    internal async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));
        await File.WriteAllTextAsync(path, BuildCsv(ct), ct).ConfigureAwait(false);
        _logger.LogInformation("Exported bot sentiment rows to {Path}.", path);
        return path;
    }
    #endregion
}

internal readonly record struct SentimentSample(
    DateTime TimestampUtc,
    int      StockId,
    decimal  Combined,
    decimal  GlobalSum,
    decimal  Shock);
