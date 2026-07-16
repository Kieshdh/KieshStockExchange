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
    private readonly double _residualHalfLifeSec; // §news-permanence: slow bleed of the permanent floor (~3h ⇒ session-permanent, not eternal)
    private readonly double _cap;          // max |shock| as a fraction of seed
    private readonly double _floor;        // drop a decaying shock once it shrinks below this
    private readonly double _softWallK;    // cubic soft-wall strength near ±cap
    private readonly double _difficultyMult; // reserved product dial (1.0 = inert): scales impulse magnitude

    // §news-permanence: per-stock shock decomposed into a TRANSIENT overshoot (decays at the per-event Tau) and a
    // PERMANENT residual floor (decays slowly at ResidualHalfLifeSec). Legacy impulses (α=0/τ=0 sentinel) put the whole
    // step into Transient at the global half-life ⇒ Residual stays 0 ⇒ byte-identical to the pre-permanence engine.
    private struct ShockState { public double Transient; public double Residual; public double TauSec; }
    // Per-stock signed shock state; only entries with |transient| ≥ floor OR |residual| ≥ floor are kept. shockId
    // persists across decay so a fresh impulse from rest increments it (reshuffling the chaser cohort).
    private readonly Dictionary<int, ShockState> _shock   = new();
    private readonly Dictionary<int, int>        _shockId = new();
    private int  _activeCount;
    private int  _arrivalsSinceLog;
    private long _simTick;
    private int  _globalCoFireSign; // ±1 on the tick a global impulse fires, else 0 (relayed from the source).
    private int  _globalCoFireSector = -1; // 0..N−1 the sector a global pulse scoped to this tick, else −1 (relayed from the source).
    private int  _globalPulseId;    // monotonic id per global pulse — reshuffles the co-fire cohort + spread.

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
        double floor = 0.001, double softWallK = 0.1, double difficultyMult = 1.0,
        double residualHalfLifeSec = 10800.0)
    {
        _stocks   = stocks   ?? throw new ArgumentNullException(nameof(stocks));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _source   = source   ?? throw new ArgumentNullException(nameof(source));
        _enabled  = enabled;
        _decayHalfLifeSec = Math.Max(MinDtSec, decayHalfLifeSec);
        _residualHalfLifeSec = Math.Max(MinDtSec, residualHalfLifeSec);
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

        // 1) Decay each entry: the TRANSIENT overshoot bleeds at its per-event half-life toward the raised floor,
        //    the PERMANENT residual bleeds slowly toward 0 (spec §1.3). Legacy sentinel entries hold Residual=0 and
        //    TauSec=global half-life ⇒ this is bit-for-bit the old single-accumulator decay. Drop only when BOTH
        //    components fall below the floor. residual *= keep with residual==0 stays 0 ⇒ no round-trip drift off.
        if (_shock.Count > 0)
        {
            double keepR = Math.Pow(0.5, dt / _residualHalfLifeSec);
            foreach (var sid in _shock.Keys.ToList())
            {
                var e = _shock[sid];
                double tau = e.TauSec > 0.0 ? e.TauSec : _decayHalfLifeSec;
                e.Transient *= Math.Pow(0.5, dt / tau);
                e.Residual  *= keepR;
                if (Math.Abs(e.Transient) < _floor && Math.Abs(e.Residual) < _floor) _shock.Remove(sid);
                else _shock[sid] = e;
            }
        }

        // 2) Apply arrivals from the source. shockId bumps ONLY on a genuine new-impulse-from-rest (hysteresis),
        //    so a shock hovering at the floor can't reshuffle the chaser cohort every tick. The soft-wall clamps the
        //    TOTAL (transient+residual) to ±Cap; the applied step is then split α→residual / (1−α)→transient.
        foreach (var imp in _source.Poll(_simTick, dt))
        {
            int sid = imp.StockId;
            bool wasAtRest = !_shock.TryGetValue(sid, out var e);
            double prevTotal = e.Transient + e.Residual; // 0 when at rest (default struct)
            double next = BotMath.SoftWallStep(prevTotal, imp.SignedMagnitude * Mult(sid), _cap, _softWallK);
            if (Math.Abs(next) < _floor) continue; // negligible after the wall — ignore
            if (imp.DecayHalfLifeSec <= 0.0)
            {
                // Legacy sentinel: the WHOLE clamped step is transient at the global half-life — EXACT pre-permanence
                // assignment (next, not prevTotal+applied) so there is no floating-point round-trip ⇒ byte-identical.
                e.Transient = next;
                e.TauSec    = _decayHalfLifeSec;
            }
            else
            {
                double applied = next - prevTotal; // the effective step after the joint soft-wall
                e.Residual  += imp.PermanentFraction * applied;        // α·M → permanent floor
                e.Transient += (1.0 - imp.PermanentFraction) * applied; // (1−α)·M → transient overshoot
                e.TauSec     = imp.DecayHalfLifeSec;                    // newer event wins (refractory: no parallel floor)
            }
            _shock[sid] = e;
            if (wasAtRest) _shockId[sid] = _shockId.GetValueOrDefault(sid) + 1;
            _arrivalsSinceLog++;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var sym = _stocks.TryGetSymbol(sid, out var s) ? s : sid.ToString(CultureInfo.InvariantCulture);
                _logger.LogInformation("ExogShock: {Symbol} {Delta:+0.000;-0.000}", sym, next);
            }
        }

        // §global co-fire: relay the source's shared-impulse sign for THIS tick so the chaser can fire a
        // simultaneous, same-sign taker burst across all stocks (correlated flow). 0 on non-pulse ticks.
        _globalCoFireSign = _source.LastGlobalSign;
        _globalCoFireSector = _source.LastGlobalSector; // −1 = market-wide pulse; 0..N−1 = sector-scoped ⇒ chaser restricts to it.
        if (_globalCoFireSign != 0) _globalPulseId++;

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
        _globalCoFireSign = 0;
        _globalCoFireSector = -1;
        _globalPulseId = 0;
        _source.Reset();
        lock (_samples) _samples.Clear();
        _lastTickUtc = _enabled ? now : DateTime.MaxValue;
        _nextLogUtc  = now + TimeSpan.FromSeconds(60);
        _logger.LogDebug("ExogenousShockService reset (enabled={Enabled}, cap={Cap}, halfLife={HL}s).",
            _enabled, _cap, _decayHalfLifeSec);
    }
    #endregion

    #region Reads (hot path — loop-thread only)
    /// <summary>Signed TOTAL shock (transient + permanent residual, fraction of seed) for a stock; 0 when disabled,
    /// unseen, or at rest. Feeds the FundamentalService anchor tilt (so the raised residual re-rates the level).</summary>
    internal double GetShock(int stockId)
        => _enabled && _shock.TryGetValue(stockId, out var e) ? e.Transient + e.Residual : 0.0;

    /// <summary>§news-permanence: the TRANSIENT overshoot only (excludes the permanent residual). Feeds the chaser
    /// cohort so it chases the fresh burst that fades at τ½, NOT the durable floor (which needs no perpetual taker
    /// flow — the anchor holds it). Equals <see cref="GetShock"/> when permanence is off (residual is always 0).</summary>
    internal double GetTransient(int stockId)
        => _enabled && _shock.TryGetValue(stockId, out var e) ? e.Transient : 0.0;

    /// <summary>Monotonic impulse-generation id for a stock (0 when none) — keys the per-shock chaser reshuffle.</summary>
    internal int GetShockId(int stockId)
        => _shockId.TryGetValue(stockId, out var v) ? v : 0;

    /// <summary>±1 when a MARKET-WIDE impulse fired THIS tick (else 0) — the global co-fire signal for the chaser
    /// (all co-firers act same-tick, same-sign ⇒ correlated taker flow). 0 when the service is disabled.</summary>
    internal int GlobalCoFireSign => _enabled ? _globalCoFireSign : 0;

    /// <summary>The sector (0..N−1) THIS tick's global pulse was scoped to, or −1 for market-wide / none — restricts the
    /// co-fire cohort to one sector ⇒ intra-sector correlated flow. −1 when the service is disabled ⇒ no sector filtering.</summary>
    internal int GlobalCoFireSector => _enabled ? _globalCoFireSector : -1;

    /// <summary>Monotonic id per global pulse — keys the co-fire cohort + per-bot stock spread so each pulse reshuffles.</summary>
    internal int GlobalPulseId => _globalPulseId;
    #endregion

    #region Telemetry
    private void LogSummary(DateTime now)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        double maxAbs = 0.0, sumAbs = 0.0;
        foreach (var e in _shock.Values) { var a = Math.Abs(e.Transient + e.Residual); sumAbs += a; if (a > maxAbs) maxAbs = a; }
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
                _shock.TryGetValue(sid, out var e);
                double total = e.Transient + e.Residual;
                bool active = Math.Abs(e.Transient) >= _floor || Math.Abs(e.Residual) >= _floor;
                var sample = new ShockSample(now, sid, (decimal)total, (decimal)e.Residual, GetShockId(sid), active);
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

        var sb = new StringBuilder(256 + snapshot.Length * 56);
        sb.AppendLine("TimestampUtc,StockId,Shock,Residual,ShockId,Active");
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = snapshot[i];
            sb.Append(r.TimestampUtc.ToString("O", inv)).Append(',')
              .Append(r.StockId).Append(',')
              .Append(r.Shock.ToString(inv)).Append(',')
              .Append(r.Residual.ToString(inv)).Append(',')
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
    decimal  Shock,      // total = transient + residual
    decimal  Residual,   // §news-permanence: the permanent floor component only
    int      ShockId,
    bool     Active);
