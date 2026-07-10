using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §bank-estimate: a "dominant house analyst" that periodically republishes a per-stock fair-value ESTIMATE
/// (expressed as a fractional deviation from the seed price). The slow value-anchor
/// (<see cref="FundamentalService"/>) is pivoted to track this estimate instead of the raw seed, and the
/// rotational cohort (<see cref="RotatorDecisionService"/>) trades INTO the price-vs-estimate gap — together the
/// value (fundamental) and momentum (taker-flow) legs of one coupled mechanism. A sector re-rating makes the
/// whole sector's estimate move together ⇒ the cohort rotates that sector together ⇒ correlated flow with a
/// causal "why", and it is self-stabilising (once price reaches the estimate the gap ⇒ 0).
///
/// Design musts (council): (1) IRREGULAR Poisson republish timing — a clockwork lurch is a rigged-pump tell;
/// (2) the estimate must sometimes be WRONG (idiosyncratic variance) — the GAP is the tradeable feature, not a
/// leash; (3) SYMMETRIC revisions — the per-stock sentiment input is ZERO-MEANED (its rolling mean subtracted)
/// so the DipBuy positive skew can't ratchet estimates up into a new up-drift source.
///
/// Conservation-neutral (places no orders, holds no balances — only shifts the price bots aim at). Deterministic:
/// a dedicated RNG drawn ONLY when enabled, over a stable stock iteration, so "off" is byte-identical and runs
/// replay. Loop-thread only: <see cref="Tick"/> mutates the dictionaries, so <see cref="BankTarget"/>/
/// <see cref="PrevBankTarget"/> must NEVER be called from OnQuoteUpdated/broadcaster threads. Disabled ⇒
/// <see cref="BankTarget"/> returns 0 and <see cref="Tick"/> is a no-op.
/// </summary>
internal sealed class BankEstimateService
{
    // Deterministic seed, distinct from sentiment(43)/fx(47)/regime(53)/fundamental(71)/shock(89).
    private const int RngSeed = 101;

    private const double MinDtSec = 0.05;
    private const double MaxDtSec = 60.0;

    // ±1 of zero-meaned sentiment maps to this fractional deviation of seed (keeps the estimate in band units).
    private const double SentimentToDevScale = 0.05;
    // Absolute safety clamp on the published estimate (FundamentalService additionally clamps to its inner band;
    // this keeps the value the Rotator's gap sees bounded/cross-stock comparable).
    private const double MaxDev = 0.12;
    // Anti-pump: the estimate cannot move more than this fraction of seed per republish, so price-convergence
    // lag stays below the republish interval (no oscillation / synthetic pump).
    private const double AntiPumpMaxStepPerRepublish = 0.02;
    // Sector shared-drift bounded walk (co-rerating). Only active when SectorCount > 1.
    private const double SectorDriftCap = 0.03;
    private const double SectorDriftSoftWallK = 0.1;

    private readonly IStockService _stocks;
    private readonly ISectorMap? _sectorMap;        // §sector: real stock→sector map; null/empty ⇒ modulo fallback
    private readonly StockProfileService _profiles;
    private readonly BotSentimentService _sentiment;
    private readonly ILogger<BankEstimateService> _logger;
    private readonly Func<int, double>? _exogShock; // fold live news into the estimate (existing shock delegate)

    private readonly bool   _enabled;
    private readonly double _alpha;                 // weight on the (zero-meaned) sentiment vs the prior estimate
    private readonly double _poissonMeanIntervalSec;
    private readonly double _wrongnessFraction;     // idiosyncratic variance scale (the estimate is sometimes wrong)
    private readonly int    _sectorCount;           // fallback modulo count (used only when the real map is absent)
    private readonly bool   _useRealSectors;        // gate the real map (false ⇒ force the config-modulo path even if seeded)
    // §soak: publish an estimate for EVERY stock on the FIRST tick (bypass the Poisson dribble) so short A/B soaks
    // aren't starved to the ~few stocks that have arrived yet. Prod doesn't need it (it warms over its long run);
    // default off ⇒ byte-identical to the Poisson-only path.
    private readonly bool   _seedAllOnStart;
    private bool            _seededAll;

    // Per-stock published estimate + its previous published value (for the Rotator's estimate-velocity term).
    private readonly Dictionary<int, double> _estimate     = new();
    private readonly Dictionary<int, double> _prevEstimate = new();
    // Per-stock EWMA of raw sentiment — subtracted to zero-mean the estimate input (symmetric revisions).
    private readonly Dictionary<int, double> _sentMean     = new();
    // Per-sector shared drift (the sector re-rating factor), keyed by stockId % SectorCount.
    private readonly Dictionary<int, double> _sectorDrift  = new();

    private Random _rng = new(RngSeed);
    private DateTime _lastTickUtc = DateTime.MaxValue; // inert until Reset arms the clock
    private DateTime _nextLogUtc  = DateTime.MaxValue;
    private long _simTick;
    private int  _republishesSinceLog;

    internal BankEstimateService(IStockService stocks, StockProfileService profiles,
        BotSentimentService sentiment, ILogger<BankEstimateService> logger,
        bool enabled = false, double alpha = 0.3, double poissonMeanIntervalSec = 30.0,
        double wrongnessFraction = 0.15, ISectorMap? sectorMap = null, int sectorCount = 1,
        Func<int, double>? exogShock = null, bool useRealSectors = true, bool seedAllOnStart = false)
    {
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _sectorMap = sectorMap;
        _profiles  = profiles  ?? throw new ArgumentNullException(nameof(profiles));
        _sentiment = sentiment ?? throw new ArgumentNullException(nameof(sentiment));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _exogShock = exogShock;
        _enabled   = enabled;
        _alpha     = Math.Clamp(alpha, 0.0, 1.0);
        _poissonMeanIntervalSec = Math.Max(0.01, poissonMeanIntervalSec);
        _wrongnessFraction = Math.Max(0.0, wrongnessFraction);
        _sectorCount = Math.Max(1, sectorCount);
        _useRealSectors = useRealSectors;
        _seedAllOnStart = seedAllOnStart;
    }

    // §sector: the real map supersedes the config modulo count once sectors are seeded. Built lazily, so it is
    // evaluated per-Tick (cheap after the first build) rather than snapshotted before the catalog loads. No real
    // sectors ⇒ these fall back to the config modulo path ⇒ byte-identical to the pre-feature engine.
    private bool UseRealSectors => _useRealSectors && _sectorMap is { HasRealSectors: true };
    private int EffectiveSectorCount => UseRealSectors ? _sectorMap!.SectorCount : _sectorCount;
    // Stable per-sector index (the RNG-walk key). Ordinal from the real map, else stockId % modulo count.
    private int SectorOrdinal(int stockId) => UseRealSectors ? _sectorMap!.OrdinalOf(stockId) : stockId % _sectorCount;

    /// <summary>Fractional deviation from seed of the current published estimate (0 when disabled/unseen).</summary>
    internal double BankTarget(int stockId)
        => _enabled && _estimate.TryGetValue(stockId, out var v) ? v : 0.0;

    /// <summary>The previous published estimate deviation (for the estimate-velocity term); 0 when unseen.</summary>
    internal double PrevBankTarget(int stockId)
        => _enabled && _prevEstimate.TryGetValue(stockId, out var v) ? v : 0.0;

    /// <summary>§soak/test: how many stocks have a published estimate so far (all of them after a seed-all first tick).</summary>
    internal int PublishedCount => _estimate.Count;

    /// <summary>Advance the rolling sentiment mean + sector drift, and republish estimates on Poisson arrivals.
    /// No-op (and no RNG draws) when disabled ⇒ byte-identical to the pre-feature engine.</summary>
    internal void Tick(DateTime now)
    {
        if (!_enabled || _lastTickUtc == DateTime.MaxValue) return;
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastTickUtc = now;
        _simTick++;

        // 1) Sector shared-drift bounded walk (only when sectors are enabled). One draw per sector, fixed order —
        //    the ordinal order (real map or modulo) is stable ⇒ reproducible draw sequence.
        int sectorCount = EffectiveSectorCount;
        if (sectorCount > 1)
        {
            for (int sector = 0; sector < sectorCount; sector++)
            {
                double prev = _sectorDrift.GetValueOrDefault(sector);
                double step = (_rng.NextDouble() * 2.0 - 1.0) * _wrongnessFraction * 0.01;
                _sectorDrift[sector] = BotMath.SoftWallStep(prev, step, SectorDriftCap, SectorDriftSoftWallK);
            }
        }

        // 2) Per-stock: maintain the zero-mean sentiment EWMA, then republish on a Poisson arrival.
        double keep = Math.Pow(0.5, dt / Math.Max(MinDtSec, _poissonMeanIntervalSec * 4.0)); // slow mean
        double pArrival = 1.0 - Math.Exp(-dt / _poissonMeanIntervalSec);
        bool seedAll = _seedAllOnStart && !_seededAll; // §soak: first tick republishes EVERY stock (no Poisson dribble)
        foreach (var sid in _stocks.ById.Keys) // stable iteration ⇒ reproducible draw sequence
        {
            double sent = (double)_sentiment.GetSentiment(sid);
            if (_sentMean.TryGetValue(sid, out var mean)) _sentMean[sid] = mean * keep + sent * (1.0 - keep);
            else _sentMean[sid] = sent; // cold-start ⇒ centered ≈ 0 initially

            if (!seedAll && _rng.NextDouble() >= pArrival) continue; // draw 1: republish arrival (skipped on seed-all)

            double centered = sent - _sentMean[sid];
            double sigmaMult = (double)_profiles.Get(sid).FundamentalSigmaMult;
            double variance = (_rng.NextDouble() * 2.0 - 1.0) * _wrongnessFraction * SentimentToDevScale * sigmaMult; // draw 2
            int ordinal = sectorCount > 1 ? SectorOrdinal(sid) : -1;
            double sectorTerm = ordinal >= 0 ? _sectorDrift.GetValueOrDefault(ordinal) : 0.0;
            double news = _exogShock?.Invoke(sid) ?? 0.0;

            double prevDev = _estimate.GetValueOrDefault(sid);
            double rawDev = _alpha * (centered * SentimentToDevScale * sigmaMult)
                          + (1.0 - _alpha) * prevDev
                          + sectorTerm + variance + news;

            // Anti-pump: bound the per-republish step, then the absolute deviation.
            double delta = Math.Clamp(rawDev - prevDev, -AntiPumpMaxStepPerRepublish, AntiPumpMaxStepPerRepublish);
            double newDev = Math.Clamp(prevDev + delta, -MaxDev, MaxDev);

            _prevEstimate[sid] = prevDev;
            _estimate[sid] = newDev;
            _republishesSinceLog++;
        }
        if (seedAll) _seededAll = true; // one-shot: subsequent ticks resume the normal Poisson cadence

        if (now >= _nextLogUtc) { LogSummary(now); _nextLogUtc = now + TimeSpan.FromSeconds(60); }
    }

    /// <summary>Clear all estimate state, reseed the RNG, and arm the tick clock (inert when disabled).</summary>
    internal void Reset(DateTime now)
    {
        _rng = new Random(RngSeed);
        _estimate.Clear();
        _prevEstimate.Clear();
        _sentMean.Clear();
        _sectorDrift.Clear();
        _simTick = 0;
        _republishesSinceLog = 0;
        _seededAll = false;
        _lastTickUtc = _enabled ? now : DateTime.MaxValue;
        _nextLogUtc  = now + TimeSpan.FromSeconds(60);
        _logger.LogDebug("BankEstimateService reset (enabled={Enabled}, alpha={Alpha}, meanInterval={Mean}s, sectors={Sectors}).",
            _enabled, _alpha, _poissonMeanIntervalSec, _sectorCount);
    }

    private void LogSummary(DateTime now)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        double maxAbs = 0.0, sumAbs = 0.0;
        foreach (var v in _estimate.Values) { var a = Math.Abs(v); sumAbs += a; if (a > maxAbs) maxAbs = a; }
        int n = _estimate.Count;
        _logger.LogInformation(
            "BankEstimate @ {Time} republishes={Rep} stocks={N} max|dev|={Max:0.000} mean|dev|={Mean:0.000}",
            now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), _republishesSinceLog, n, maxAbs,
            n > 0 ? sumAbs / n : 0.0);
        _republishesSinceLog = 0;
    }
}
