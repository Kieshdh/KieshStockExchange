using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §P6 liveliness: a slowly-drifting per-(stock,currency) fundamental that the bot value anchor tracks,
/// instead of a fixed seed price. Each fundamental is an Ornstein–Uhlenbeck walk that reverts to the
/// seed with a multi-hour time-constant and is hard-clamped to <c>seed × [1−Band, 1+Band]</c>, so it
/// adds genuine long-horizon liveliness (a stock can trend over a session) while staying bounded — it
/// can never itself run away. Per-stock σ is scaled by the stock's personality (calm names barely move,
/// meme names drift more).
///
/// Conservation-neutral: places no orders, holds no balances — it only shifts the price bots *aim at*.
/// Deterministic RNG so runs reproduce. Driven by one <see cref="Tick"/> per bot-loop iteration (~1 Hz);
/// drift steps are internally gated to <c>DriftIntervalSeconds</c>. Tick/Get run on the loop thread.
/// When disabled, <see cref="Get"/> returns the fixed seed — identical to the pre-P6 behaviour.
/// </summary>
internal sealed class FundamentalService
{
    private const int RngSeed = 71; // deterministic, reproducible across runs

    private readonly IStockService _stocks;
    private readonly StockProfileService _profiles;
    private readonly ILogger<FundamentalService> _logger;

    private readonly bool _enabled;
    private readonly decimal _band;        // max fractional excursion from seed (e.g. 0.12)
    private readonly double  _theta;       // mean-reversion pull per drift step (small → slow)
    private readonly double  _sigma;       // per-step shock as a fraction of seed
    private readonly double  _driftIntervalSec;

    // §exogenous-information: when wired (AnchorTracksShock on), the anchor TARGET = current × (1 + shock),
    // bounded to seed × [1 ± (Band + ShockCap)]. Null ⇒ the OU walk only ⇒ byte-identical to the pre-feature
    // engine. Composition is at READ time only (Tick/Gaussian untouched), so the OU RNG stream is identical
    // on AND off. _anyShockActive is a cheap global fast-path to skip composition when no shock is live.
    private readonly Func<int, double>? _exogShock;
    private readonly Func<bool>?        _anyShockActive;
    private readonly decimal            _shockCap;
    private const double ShockFloorEpsilon = 1e-6; // near-rest decay dust ⇒ return legacy value (byte-identical)

    // §co-movement: a SHARED market-factor fractional shift (Strength×beta×factor, supplied by
    // BotSentimentService) composed onto the anchor TARGET at read time so all stocks' fundamentals move
    // together → the value-anchor pulls them in lockstep → cross-stock co-movement. Read-time only (OU/RNG
    // stream untouched). Null ⇒ no composition ⇒ byte-identical. _coMoveShiftCap = extra excursion headroom.
    private readonly Func<int, double>? _coMoveShift;
    private readonly decimal            _coMoveShiftCap;

    // §bank-estimate: when wired (Bots:BankEstimate:Enabled), the OU reversion TARGET is the bank's published
    // per-stock estimate (a fractional deviation from seed) instead of the raw seed. The estimate is clamped
    // INTERIOR to the existing hard band (seed × [1 ± Band·EstimateTargetInnerBand]) so the OU can still diffuse
    // around it — a target parked at the hard band would kill diffusion variance. Null ⇒ the target stays the
    // seed ⇒ byte-identical to the pre-feature engine. Applied in Tick only (the gaussian term still scales by
    // seed, so diffusion magnitude is unchanged).
    private readonly Func<int, double>? _bankTarget;
    private const decimal EstimateTargetInnerBand = 0.8m; // estimate lives inside 80% of the hard band

    private readonly Dictionary<(int, CurrencyType), decimal> _seed = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _current = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _sigmaMult = new();

    private Random _rng = new(RngSeed);
    private DateTime _lastDriftUtc = DateTime.MaxValue; // MaxValue = inert until Reset

    internal FundamentalService(IStockService stocks, StockProfileService profiles,
        ILogger<FundamentalService> logger, bool enabled = true, decimal band = 0.12m,
        double theta = 0.02, double sigma = 0.004, double driftIntervalSec = 60.0,
        Func<int, double>? exogShock = null, Func<bool>? anyShockActive = null, decimal shockCap = 0m,
        Func<int, double>? coMoveShift = null, decimal coMoveShiftCap = 0m,
        Func<int, double>? bankTarget = null)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = enabled;
        _band = band <= 0m ? 0.12m : band;
        _theta = Math.Clamp(theta, 0.0, 1.0);
        _sigma = Math.Max(0.0, sigma);
        _driftIntervalSec = Math.Max(1.0, driftIntervalSec);
        _exogShock = exogShock;
        _anyShockActive = anyShockActive;
        _shockCap = Math.Max(0m, shockCap);
        _coMoveShift = coMoveShift;
        _coMoveShiftCap = Math.Max(0m, coMoveShiftCap);
        _bankTarget = bankTarget;
    }

    /// <summary>Seed every (stock,currency) fundamental at its listing seed price and arm the clock.</summary>
    internal void Reset()
    {
        _rng = new Random(RngSeed);
        _seed.Clear();
        _current.Clear();
        _sigmaMult.Clear();

        foreach (var sid in _stocks.ById.Keys)
        {
            var mult = (double)_profiles.Get(sid).FundamentalSigmaMult;
            foreach (var l in _stocks.GetListings(sid))
            {
                if (l.SeedPrice <= 0m) continue;
                var key = (sid, l.CurrencyType);
                _seed[key] = l.SeedPrice;
                _current[key] = l.SeedPrice;
                _sigmaMult[key] = (decimal)mult;
            }
        }

        _lastDriftUtc = _enabled ? TimeHelper.NowUtc() : DateTime.MaxValue;
        _logger.LogDebug("FundamentalService reset: {Count} (stock,ccy) fundamentals seeded (enabled={Enabled}).",
            _seed.Count, _enabled);
    }

    /// <summary>Advance the OU walk when a drift interval has elapsed. Cheap no-op otherwise.</summary>
    internal void Tick(DateTime now)
    {
        if (!_enabled || _lastDriftUtc == DateTime.MaxValue) return;
        if ((now - _lastDriftUtc).TotalSeconds < _driftIntervalSec) return;
        _lastDriftUtc = now;

        foreach (var key in _current.Keys.ToList())
        {
            var seed = _seed[key];
            if (seed <= 0m) continue;
            var f = (double)_current[key];
            var s = (double)seed;
            var sigmaMult = _sigmaMult.TryGetValue(key, out var m) ? (double)m : 1.0;

            // §bank-estimate: the reversion target is the bank's published estimate (a fractional deviation
            // from seed), clamped INTERIOR to the hard band (±Band·0.8) so the OU keeps diffusion room around
            // it — parking the target at the hard band would kill diffusion variance. Null/at-rest ⇒ target = seed
            // ⇒ byte-identical. This is the ONLY site the estimate is bounded into the anchor.
            double target = s;
            if (_bankTarget is not null)
            {
                double inner = (double)(_band * EstimateTargetInnerBand);
                double dev = Math.Clamp(_bankTarget(key.Item1), -inner, inner);
                target = s * (1.0 + dev);
            }

            // OU step: pull toward the target + scaled gaussian shock. The gaussian stays SEED-scaled (s), so the
            // diffusion magnitude is independent of where the estimate has moved the target.
            f += _theta * (target - f) + _sigma * sigmaMult * s * Gaussian();

            // Hard band clamp so the fundamental itself can never run away.
            var lo = s * (1.0 - (double)_band);
            var hi = s * (1.0 + (double)_band);
            f = Math.Clamp(f, lo, hi);

            _current[key] = (decimal)f;
        }
    }

    /// <summary>
    /// Current fundamental for (stock,currency); the fixed seed when disabled or unseeded. When the anchor
    /// tracks the exogenous shock, the returned target is <c>current × (1 + shock)</c>, hard-bounded to
    /// <c>seed × [1 ± (Band + ShockCap)]</c>. Byte-identical to the legacy value whenever the shock is unwired
    /// or at rest (the floor-epsilon early-out guards against decimal round-trip noise).
    /// </summary>
    internal decimal Get(int stockId, CurrencyType currency)
    {
        var key = (stockId, currency);
        if (_enabled && _current.TryGetValue(key, out var f))
        {
            // Compose, at READ time only, the live news shock and/or the shared co-movement shift onto the
            // OU value f (the OU walk + RNG stream stay untouched ⇒ identical on AND off). Each composition
            // is skipped when its source is unwired/at-rest, so the all-off path returns f unchanged —
            // byte-identical to the legacy engine.
            decimal target = f;
            if (_exogShock is not null && (_anyShockActive is null || _anyShockActive()))
            {
                double shock = _exogShock(stockId);
                if (Math.Abs(shock) >= ShockFloorEpsilon) target *= (1m + (decimal)shock);
            }
            if (_coMoveShift is not null)
            {
                double cm = _coMoveShift(stockId);
                if (cm != 0.0) target *= (1m + (decimal)cm);    // shared market-factor shift ⇒ stocks co-move
            }
            if (target == f) return f;                          // nothing composed ⇒ legacy OU value
            var seed = _seed[key];
            var span = _band + _shockCap + _coMoveShiftCap;      // total allowed excursion from seed
            var lo = seed * (1m - span);
            var hi = seed * (1m + span);
            return target < lo ? lo : target > hi ? hi : target;
        }
        return _seed.TryGetValue(key, out var s) ? s : 0m;
    }

    // Standard normal via Box–Muller (shared impl; same two-draw order on _rng).
    private double Gaussian() => BotMath.NextGaussian(_rng);
}
