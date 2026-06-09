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

    private const double Sqrt3 = 1.7320508075688772; // makes U(-1,1)*Sqrt3 unit-variance

    // Deterministic seed so the simulation is reproducible across runs.
    private const int RngSeed = 43;

    // Clamp the per-tick elapsed time so a stalled or first-after-reset loop can't distort α.
    private const double MinDtSec = 0.05;
    private const double MaxDtSec = 60.0;

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
        double shockMagnitudeExponent = 3.0, decimal shockDecayPerTick = 0.999m)
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
            gNoise[k] = GlobalSigma[k] * Math.Sqrt(1.0 - a * a);
        }
        Span<double> sAlpha = stackalloc double[PerStockRings];
        Span<double> sNoise = stackalloc double[PerStockRings];
        for (int k = 0; k < PerStockRings; k++)
        {
            double a = Math.Exp(-dt / PerStockTauSec[k]);
            sAlpha[k] = a;
            sNoise[k] = PerStockSigma[k] * Math.Sqrt(1.0 - a * a);
        }

        // Global ring (computed once; common-mode across all stocks).
        _globalSum = 0.0;
        for (int k = 0; k < GlobalRings; k++)
        {
            _global[k] = gAlpha[k] * _global[k] + gNoise[k] * UnitNoise();
            _globalSum += _global[k];
        }

        if (_newsEvents) StepShocks();

        // Per-stock rings + combined cache.
        foreach (var sid in _stocks.ById.Keys)
        {
            if (!_perStock.TryGetValue(sid, out var ring)) { ring = new double[PerStockRings]; _perStock[sid] = ring; }
            double amp = AmpMult(sid);
            double sum = _globalSum;
            for (int k = 0; k < PerStockRings; k++)
            {
                ring[k] = sAlpha[k] * ring[k] + sNoise[k] * amp * UnitNoise();
                sum += ring[k];
            }
            if (_shock.TryGetValue(sid, out var sh)) sum += sh;
            _combined[sid] = (decimal)sum;
        }

        if (now >= _nextLogUtc) { LogCombinedSentiment(now); _nextLogUtc = now + TimeSpan.FromSeconds(60); }
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
    /// Combined sentiment for <paramref name="stockId"/> (per-stock ring + global ring + news
    /// shock), read from the cache Tick maintains. Un-clamped: typically ±1, more during a shock.
    /// </summary>
    internal decimal GetSentiment(int stockId)
        => _combined.TryGetValue(stockId, out var v) ? v : 0m;

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
