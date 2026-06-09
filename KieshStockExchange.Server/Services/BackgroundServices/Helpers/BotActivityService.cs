using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §Pillar B — a self-exciting market-ACTIVITY field that makes volume cluster and breathe instead of
/// running flat. A positive multiplier on participation, mean ≈ 1, decomposed multiplicatively:
/// <code>A(bot, stock, t) = G(t) · S(stock, t) · B(bot, t)</code>
/// where <b>G·B</b> modulates how OFTEN a bot trades (the gate seam) and <b>S</b> modulates WHICH name
/// catches the volume (the PickStock weight). Sibling of <see cref="BotSentimentService"/>: one
/// <see cref="Tick"/> per bot-loop iteration on the loop thread (no locks), a seeded <c>_rng</c> with its
/// OWN seed, a <see cref="Reset"/>, and per-stock caches for O(1) hot-path reads.
///
/// <para>v1 deviations from <c>docs/bot-variable-volume.md</c> (documented, conservative): the global and
/// per-stock rings are <b>single-timescale</b> AR(1) (not full fast→slow rings) and the Hawkes kernel uses
/// a <b>single decay</b> ρ (branching ratio <c>n = w_self/(1−ρ)</c>); both are noted as the v1
/// approximation in the spec §1.2/§6. Telemetry is a periodic log line (no NDJSON/CSV surface) to keep the
/// change inside the decision layer.</para>
///
/// <para>Inert when disabled: <see cref="Tick"/> is a no-op (RNG never advances) and every read returns 1,
/// so the bot order stream is byte-identical to today.</para>
/// </summary>
internal sealed class BotActivityService
{
    #region Configuration / seed
    private const int RngSeed = 59; // deterministic, distinct from sentiment(43)/fx(47)/fundamental(71)/regime(53)
    private const double MinDtSec = 0.05;
    private const double MaxDtSec = 60.0;
    private const double Sqrt3    = 1.7320508075688772; // U(-1,1)*√3 == unit variance

    private static readonly TimeSpan BDriftPeriod = TimeSpan.FromMinutes(15);
    #endregion

    #region Tunables (service-level; defaults per bot-variable-volume.md §4)
    private readonly bool   _enabled;
    private readonly double _activityBaseline; // calm-regime center (<1 = quieter lulls)
    private readonly double _globalTauSec, _globalSigma;
    private readonly double _perStockTauSec, _perStockSigma;
    private readonly double _floor, _sMax;
    private readonly double _wNews, _wMoveUp, _wMoveDown, _wSent, _theta, _wSelf, _decay;
    private readonly double _bDriftAmp;
    #endregion

    #region State
    private double _g;                                           // global AR(1) log-score
    private readonly Dictionary<int, double> _sBase = new();     // per-stock baseline AR(1) log-score
    private readonly Dictionary<int, double> _h     = new();     // per-stock decaying Hawkes intensity
    private readonly Dictionary<int, double> _sCache = new();    // per-stock clamped S (hot-path read)
    private readonly Dictionary<int, long>   _fills  = new();    // per-stock fills since last Tick (drained)
    private double _gCache = 1.0;                                // cached G (hot-path read)

    private DateTime _lastTickUtc = DateTime.MaxValue;           // inert until Reset arms the clock
    private DateTime _nextLogUtc  = DateTime.MaxValue;

    private Random _rng = new(RngSeed);
    #endregion

    #region Services
    private readonly IStockService _stocks;
    private readonly BotSentimentService _sentiment;
    private readonly ILogger<BotActivityService> _logger;
    // Signed fractional return for a stock in the reference currency (USD, fallback EUR) — supplied by the
    // host so the service stays decoupled from the price cache. Read-only, no RNG, call-order-independent.
    private readonly Func<int, double> _recentReturn;
    #endregion

    internal BotActivityService(IStockService stocks, BotSentimentService sentiment,
        ILogger<BotActivityService> logger, Func<int, double> recentReturn,
        bool enabled = false, double activityBaseline = 0.6,
        double globalTauSec = 3600.0, double globalSigma = 0.20,
        double perStockTauSec = 600.0, double perStockSigma = 0.30,
        double floor = 0.2, double sMax = 6.0,
        double wNews = 0.6, double wMoveUp = 1.0, double wMoveDown = 2.0,
        double wSent = 0.3, double theta = 0.3, double wSelf = 0.009, double decay = 0.99,
        double bDriftAmp = 0.15)
    {
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _sentiment = sentiment ?? throw new ArgumentNullException(nameof(sentiment));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _recentReturn = recentReturn ?? throw new ArgumentNullException(nameof(recentReturn));
        _enabled = enabled;
        _activityBaseline = Math.Max(0.0, activityBaseline);
        _globalTauSec = Math.Max(1.0, globalTauSec);   _globalSigma = Math.Max(0.0, globalSigma);
        _perStockTauSec = Math.Max(1.0, perStockTauSec); _perStockSigma = Math.Max(0.0, perStockSigma);
        _floor = Math.Max(0.0, floor);  _sMax = Math.Max(_floor, sMax);
        _wNews = wNews; _wMoveUp = wMoveUp; _wMoveDown = wMoveDown;
        _wSent = wSent; _theta = Math.Max(0.0, theta);
        _wSelf = Math.Max(0.0, wSelf); _decay = Math.Clamp(decay, 0.0, 0.9999);
        _bDriftAmp = Math.Max(0.0, bDriftAmp);
    }

    #region Tick / Reset
    /// <summary>
    /// Advance the global + per-stock activity and refresh the caches. All ring noise uses the seeded
    /// <c>_rng</c>; every driver term (news / move / sentiment / fills) is a deterministic read of existing
    /// state, so the only RNG is the ring noise — and none of it runs when disabled.
    /// </summary>
    internal void Tick(DateTime now)
    {
        if (!_enabled) return;
        if (_lastTickUtc == DateTime.MaxValue) return; // not reset yet
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastTickUtc = now;

        // Global AR(1): α = exp(−dt/τ); steady-state std == σ.
        double gA = Math.Exp(-dt / _globalTauSec);
        _g = gA * _g + _globalSigma * Math.Sqrt(1.0 - gA * gA) * UnitNoise();
        // exp(zero-mean) is biased high by σ²/2; subtract it so the median is the baseline (mean ≈ baseline).
        _gCache = _activityBaseline * Math.Exp(_g - 0.5 * _globalSigma * _globalSigma);

        double sA = Math.Exp(-dt / _perStockTauSec);
        double sNoise = _perStockSigma * Math.Sqrt(1.0 - sA * sA);

        foreach (var sid in _stocks.ById.Keys)
        {
            // Baseline per-stock chattiness dispersion.
            double sb = _sBase.TryGetValue(sid, out var b) ? b : 0.0;
            sb = sA * sb + sNoise * UnitNoise();
            _sBase[sid] = sb;

            // Decaying self-exciting intensity (Hawkes): decay, then add exogenous + self drivers.
            double h = _h.TryGetValue(sid, out var hv) ? hv * _decay : 0.0;
            h += _wNews * _sentiment.ShockMagnitude(sid);
            double ret = _recentReturn(sid);
            h += ret >= 0 ? _wMoveUp * ret : _wMoveDown * (-ret);  // leverage asymmetry: down excites more
            double sentMag = Math.Abs((double)_sentiment.GetSentiment(sid));
            if (sentMag > _theta) h += _wSent * (sentMag - _theta);
            long fills = DrainFills(sid);
            if (fills > 0) h += _wSelf * fills;
            _h[sid] = h;

            // S = clamp(exp(baseline + h)·median-correction, Floor, S_max).
            double s = Math.Exp(sb - 0.5 * _perStockSigma * _perStockSigma + h);
            _sCache[sid] = Math.Clamp(s, _floor, _sMax);
        }

        if (now >= _nextLogUtc) { LogSnapshot(now); _nextLogUtc = now + TimeSpan.FromSeconds(60); }
    }

    private double UnitNoise() => (_rng.NextDouble() * 2.0 - 1.0) * Sqrt3;

    private long DrainFills(int stockId)
    {
        if (_fills.TryGetValue(stockId, out var n) && n != 0) { _fills[stockId] = 0; return n; }
        return 0;
    }

    /// <summary>Open neutral (field ≡ baseline/1) and arm the tick clock.</summary>
    internal void Reset(DateTime now)
    {
        _rng = new Random(RngSeed);
        _g = 0.0; _gCache = _enabled ? _activityBaseline : 1.0;
        _sBase.Clear(); _h.Clear(); _sCache.Clear(); _fills.Clear();
        _lastTickUtc = now;
        _nextLogUtc  = now + TimeSpan.FromSeconds(60);
    }
    #endregion

    #region Fill feed (self-excitation)
    /// <summary>
    /// Record fills on a stock this tick so the Hawkes kernel self-excites (trade clustering). No-op when
    /// the field is disabled. Loop-thread only (single-threaded), accumulated into a buffer that
    /// <see cref="Tick"/> drains, so there is no double-count and no lock.
    /// </summary>
    internal void RecordFill(int stockId, int quantity)
    {
        if (!_enabled || quantity <= 0) return;
        _fills[stockId] = (_fills.TryGetValue(stockId, out var n) ? n : 0) + quantity;
    }
    #endregion

    #region Reads (hot path — pure, no RNG)
    /// <summary>Global participation multiplier (1 when disabled).</summary>
    internal decimal G => _enabled ? (decimal)_gCache : 1m;

    /// <summary>Per-stock activity multiplier S (1 when disabled or unknown).</summary>
    internal decimal S(int stockId)
        => _enabled && _sCache.TryGetValue(stockId, out var s) ? (decimal)s : 1m;

    /// <summary>Mean S across a watchlist (1 when disabled / empty).</summary>
    internal decimal AverageWatchlistActivity(IReadOnlyCollection<int> watchlist)
    {
        if (!_enabled || watchlist == null || watchlist.Count == 0) return 1m;
        double sum = 0; int n = 0;
        foreach (var sid in watchlist) { sum += _sCache.TryGetValue(sid, out var s) ? s : 1.0; n++; }
        return n > 0 ? (decimal)(sum / n) : 1m;
    }

    /// <summary>
    /// Per-bot participation factor B = exp(b_drift + kStrategy·(avgWatchlistActivity − 1)). b_drift is a
    /// slow hash-based idiosyncratic drift (no stored matrix); kStrategy makes trend chasers pile into hot
    /// watchlists while value/random bots ignore short-term heat. 1 when disabled.
    /// </summary>
    internal decimal B(int aiUserId, AiStrategy strategy, IReadOnlyCollection<int> watchlist)
    {
        if (!_enabled) return 1m;
        long bucket = TimeHelper.NowUtc().Ticks / BDriftPeriod.Ticks;
        double drift = _bDriftAmp * HashSigned(aiUserId, bucket);
        double avgAct = (double)AverageWatchlistActivity(watchlist);
        double k = KStrategy(strategy);
        return (decimal)Math.Exp(drift + k * (avgAct - 1.0));
    }

    // Per-strategy gate responsiveness (bot-variable-volume.md §1.3). MarketMaker leans positive so it
    // refreshes quotes when busy (adds the depth that absorbs the move); value/random ≈ 0.
    private static double KStrategy(AiStrategy s) => s switch
    {
        AiStrategy.TrendFollower => 0.35,
        AiStrategy.Scalper       => 0.35,
        AiStrategy.MeanReversion => 0.15,
        AiStrategy.MarketMaker   => 0.15,
        _                        => 0.0,
    };

    // Pure hash → [-1,1], keyed by (aiUserId, time bucket) so the drift moves slowly and idiosyncratically.
    private static double HashSigned(int aiUserId, long bucket)
    {
        unchecked
        {
            ulong h = (ulong)aiUserId * 0x9E3779B97F4A7C15UL + (ulong)bucket * 0xC2B2AE3D27D4EB4FUL + 0x165667B19E3779F9UL;
            h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33;
            return (double)(h & 0xFFFFFFFFUL) / 4294967296.0 * 2.0 - 1.0;
        }
    }
    #endregion

    #region Logging
    private void LogSnapshot(DateTime now)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        // Report G and the few hottest names so the field is observable without an external monitor.
        int top = 0; double maxS = 0; int maxSid = 0;
        foreach (var kv in _sCache) { if (kv.Value > maxS) { maxS = kv.Value; maxSid = kv.Key; } top++; }
        var sym = _stocks.TryGetSymbol(maxSid, out var s) ? s : maxSid.ToString();
        _logger.LogInformation("Activity @ {Time} G={G:0.00} hottest={Sym}:{S:0.00} (of {N} names)",
            now.ToLocalTime().ToString("HH:mm:ss"), _gCache, sym, maxS, top);
    }
    #endregion
}
