using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §exogenous-information: a per-stock stream of signed, decaying fundamental-value innovations ("news shocks")
/// that the bot value-anchor TARGET tracks (via <see cref="FundamentalService"/>) and a chaser cohort trades
/// INTO (via <see cref="AiBotDecisionService"/>). This is the uncorrelated-information *numerator* that pulls
/// 1-min return autocorrelation toward 0 from the variance side — distinct from, and faster than, the slow
/// sentiment "news shock" inside <see cref="BotSentimentService"/> (which biases sentiment only and is neither
/// anchored-to nor chased).
///
/// State machine only: it owns decay, the bounded accumulator (cubic soft-wall + hard ±Cap clamp), the
/// per-impulse id (for the per-(bot,shock) chaser reshuffle), and telemetry. The "what arrives when" is the
/// pluggable <see cref="IShockSource"/> (random Poisson today; scripted content later) — so product features
/// land on the source side without touching this math.
///
/// Conservation-clean by construction: holds no accounts/market handle, places no orders — it only shifts the
/// price bots *aim at* and the directional signal a cohort reacts to. Deterministic (dedicated RNG inside the
/// source, drawn only when enabled). Loop-thread-only: <see cref="Tick"/> mutates the dictionaries, so
/// <see cref="GetShock"/>/<see cref="GetShockId"/>/<see cref="AnyActive"/> must NEVER be called from
/// OnQuoteUpdated/broadcasters. When disabled, <see cref="GetShock"/> returns 0 and <see cref="Tick"/> is a
/// no-op ⇒ byte-identical to the pre-feature engine.
/// </summary>
internal sealed class ExogenousShockService
{
    #region Configuration / state
    private const double MinDtSec = 0.05;
    private const double MaxDtSec = 60.0;
    private const int RecentSamplesMax = 100_000;

    private readonly IStockService _stocks;
    private readonly StockProfileService _profiles;
    private readonly ILogger<ExogenousShockService> _logger;
    private readonly IShockSource _source;

    private readonly bool   _enabled;
    private readonly double _decayHalfLifeSec;
    private readonly double _cap;          // max |shock| as a fraction of seed
    private readonly double _floor;        // drop a decaying shock once it shrinks below this
    private readonly double _softWallK;    // cubic soft-wall strength near ±cap
    private readonly double _difficultyMult; // reserved product dial (1.0 = inert): scales impulse magnitude

    // Per-stock signed shock (fraction of seed); only entries with |v| ≥ floor are kept. shockId persists
    // across decay so a fresh impulse from rest increments it (reshuffling the chaser cohort).
    private readonly Dictionary<int, double> _shock   = new();
    private readonly Dictionary<int, int>    _shockId = new();
    private int  _activeCount;
    private int  _arrivalsSinceLog;
    private long _simTick;

    private DateTime _lastTickUtc = DateTime.MaxValue; // inert until Reset arms the clock
    private DateTime _nextLogUtc  = DateTime.MaxValue;

    // Telemetry export (mirrors BotSentimentService): durable per-stock series for the soak harvester.
    private readonly Queue<ShockSample> _samples = new();
    private readonly RingBufferStore<ShockSample> _store;

    internal bool AnyActive => _activeCount > 0;

    private double Mult(int stockId) => _difficultyMult * (double)_profiles.Get(stockId).FundamentalSigmaMult;
    #endregion

    internal ExogenousShockService(IStockService stocks, StockProfileService profiles,
        ILogger<ExogenousShockService> logger, IShockSource source,
        bool enabled = false, double decayHalfLifeSec = 300.0, double cap = 0.06,
        double floor = 0.001, double softWallK = 0.1, double difficultyMult = 1.0)
    {
        _stocks   = stocks   ?? throw new ArgumentNullException(nameof(stocks));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _source   = source   ?? throw new ArgumentNullException(nameof(source));
        _enabled  = enabled;
        _decayHalfLifeSec = Math.Max(MinDtSec, decayHalfLifeSec);
        _cap       = Math.Max(0.0, cap);
        _floor     = Math.Clamp(floor, 0.0, _cap);
        _softWallK = Math.Max(0.0, softWallK);
        _difficultyMult = Math.Max(0.0, difficultyMult);

        _store = new RingBufferStore<ShockSample>("data/telemetry/bot_exog_shock.ndjson");
        _lastTickUtc = DateTime.MaxValue; // armed in Reset
    }

    #region Tick / Reset
    /// <summary>Decay live shocks, pull fresh impulses from the source, maintain ids + active count. No-op off.</summary>
    internal void Tick(DateTime now)
    {
        if (!_enabled || _lastTickUtc == DateTime.MaxValue) return;
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastTickUtc = now;
        _simTick++;

        // 1) Exponential decay by half-life; drop dust below the floor.
        if (_shock.Count > 0)
        {
            double keep = Math.Pow(0.5, dt / _decayHalfLifeSec);
            foreach (var sid in _shock.Keys.ToList())
            {
                double v = _shock[sid] * keep;
                if (Math.Abs(v) < _floor) _shock.Remove(sid);
                else _shock[sid] = v;
            }
        }

        // 2) Apply arrivals from the source. shockId bumps ONLY on a genuine new-impulse-from-rest (hysteresis),
        //    so a shock hovering at the floor can't reshuffle the chaser cohort every tick.
        foreach (var imp in _source.Poll(_simTick, dt))
        {
            int sid = imp.StockId;
            bool wasAtRest = !_shock.TryGetValue(sid, out var prev);
            double next = BotMath.SoftWallStep(prev, imp.SignedMagnitude * Mult(sid), _cap, _softWallK);
            if (Math.Abs(next) < _floor) continue; // negligible after the wall — ignore
            _shock[sid] = next;
            if (wasAtRest) _shockId[sid] = _shockId.GetValueOrDefault(sid) + 1;
            _arrivalsSinceLog++;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var sym = _stocks.TryGetSymbol(sid, out var s) ? s : sid.ToString(CultureInfo.InvariantCulture);
                _logger.LogInformation("ExogShock: {Symbol} {Delta:+0.000;-0.000}", sym, next);
            }
        }

        _activeCount = _shock.Count;
        if (now >= _nextLogUtc) { LogSummary(now); _nextLogUtc = now + TimeSpan.FromSeconds(60); }
    }

    /// <summary>Clear all shock state, reseed the source, and arm the tick clock (inert when disabled).</summary>
    internal void Reset(DateTime now)
    {
        _shock.Clear();
        _shockId.Clear();
        _activeCount = 0;
        _arrivalsSinceLog = 0;
        _simTick = 0;
        _source.Reset();
        lock (_samples) _samples.Clear();
        _lastTickUtc = _enabled ? now : DateTime.MaxValue;
        _nextLogUtc  = now + TimeSpan.FromSeconds(60);
        _logger.LogDebug("ExogenousShockService reset (enabled={Enabled}, cap={Cap}, halfLife={HL}s).",
            _enabled, _cap, _decayHalfLifeSec);
    }
    #endregion

    #region Reads (hot path — loop-thread only)
    /// <summary>Signed current shock (fraction of seed) for a stock; 0 when disabled, unseen, or at rest.</summary>
    internal double GetShock(int stockId)
        => _enabled && _shock.TryGetValue(stockId, out var v) ? v : 0.0;

    /// <summary>Monotonic impulse-generation id for a stock (0 when none) — keys the per-shock chaser reshuffle.</summary>
    internal int GetShockId(int stockId)
        => _shockId.TryGetValue(stockId, out var v) ? v : 0;
    #endregion

    #region Telemetry
    private void LogSummary(DateTime now)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        double maxAbs = 0.0, sumAbs = 0.0;
        foreach (var v in _shock.Values) { var a = Math.Abs(v); sumAbs += a; if (a > maxAbs) maxAbs = a; }
        int total = _stocks.ById.Count;
        double duty = total > 0 ? (double)_activeCount / total : 0.0;
        _logger.LogInformation(
            "ExogShock @ {Time} arrivals={Arr} active={Active}/{Total} duty={Duty:0.00} max|s|={Max:0.000} mean|s|={Mean:0.000}",
            now.ToLocalTime().ToString("HH:mm:ss"), _arrivalsSinceLog, _activeCount, total, duty, maxAbs,
            _activeCount > 0 ? sumAbs / _activeCount : 0.0);
        _arrivalsSinceLog = 0;
    }

    /// <summary>Append one row per known stock to the export ring (shock value, id, active flag).</summary>
    internal void LogSnapshot()
    {
        var now = TimeHelper.NowUtc();
        lock (_samples)
        {
            foreach (var sid in _stocks.ById.Keys)
            {
                _shock.TryGetValue(sid, out var v);
                var sample = new ShockSample(now, sid, (decimal)v, GetShockId(sid), Math.Abs(v) >= _floor);
                _samples.Enqueue(sample);
                _store.Append(sample);
            }
            while (_samples.Count > RecentSamplesMax) _samples.Dequeue();
        }
    }

    internal int SampleCount { get { lock (_samples) return _samples.Count; } }

    internal string SuggestedExportFileName => $"bot_exog_shock_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    internal string BuildCsv(CancellationToken ct = default)
    {
        ShockSample[] snapshot;
        lock (_samples) snapshot = _samples.ToArray();

        var sb = new StringBuilder(256 + snapshot.Length * 48);
        sb.AppendLine("TimestampUtc,StockId,Shock,ShockId,Active");
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = snapshot[i];
            sb.Append(r.TimestampUtc.ToString("O", inv)).Append(',')
              .Append(r.StockId).Append(',')
              .Append(r.Shock.ToString(inv)).Append(',')
              .Append(r.ShockId).Append(',')
              .Append(r.Active ? '1' : '0')
              .Append('\n');
        }
        return sb.ToString();
    }

    internal async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));
        await File.WriteAllTextAsync(path, BuildCsv(ct), ct).ConfigureAwait(false);
        _logger.LogInformation("Exported exogenous-shock rows to {Path}.", path);
        return path;
    }
    #endregion
}

internal readonly record struct ShockSample(
    DateTime TimestampUtc,
    int      StockId,
    decimal  Shock,
    int      ShockId,
    bool     Active);
