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

    private readonly Dictionary<(int, CurrencyType), decimal> _seed = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _current = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _sigmaMult = new();

    private Random _rng = new(RngSeed);
    private DateTime _lastDriftUtc = DateTime.MaxValue; // MaxValue = inert until Reset

    internal FundamentalService(IStockService stocks, StockProfileService profiles,
        ILogger<FundamentalService> logger, bool enabled = true, decimal band = 0.12m,
        double theta = 0.02, double sigma = 0.004, double driftIntervalSec = 60.0)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = enabled;
        _band = band <= 0m ? 0.12m : band;
        _theta = Math.Clamp(theta, 0.0, 1.0);
        _sigma = Math.Max(0.0, sigma);
        _driftIntervalSec = Math.Max(1.0, driftIntervalSec);
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

            // OU step: pull toward seed + scaled gaussian shock.
            f += _theta * (s - f) + _sigma * sigmaMult * s * Gaussian();

            // Hard band clamp so the fundamental itself can never run away.
            var lo = s * (1.0 - (double)_band);
            var hi = s * (1.0 + (double)_band);
            f = Math.Clamp(f, lo, hi);

            _current[key] = (decimal)f;
        }
    }

    /// <summary>Current fundamental for (stock,currency); the fixed seed when disabled or unseeded.</summary>
    internal decimal Get(int stockId, CurrencyType currency)
    {
        var key = (stockId, currency);
        if (_enabled && _current.TryGetValue(key, out var f)) return f;
        return _seed.TryGetValue(key, out var s) ? s : 0m;
    }

    // Standard normal via Box–Muller.
    private double Gaussian()
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
