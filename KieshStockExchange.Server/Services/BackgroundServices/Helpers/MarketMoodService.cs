using System;
using System.Collections.Generic;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Configuration;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §fear-greed: a composite Fear/Greed index (0 = extreme fear, 50 = neutral, 100 = extreme greed) for the
/// bot market. This is the FAST layer of the one-axis-three-timescales model:
///   sentiment (slow OU anchor) → F&amp;G composite (this) → activity / taker (behavioural expression).
/// Unlike v1 (a smooth <c>sentiment × activity</c> proxy, which read "too smooth") the score is dominated by
/// fast, price-derived signals so it feels alive; sentiment is demoted to a small slow anchor. READ-ONLY
/// projection: never fed back into the bot decision accumulator as a SOURCE (the reflexive lever rides a
/// separate lagged taker channel and consumes only |mood−50|).
///
/// DIRECTION signal = TREND-vs-ANCHOR (like CNN's F&amp;G momentum): <c>trend = ln(price / EMA(price))</c>,
/// normalised by the CROSS-SECTIONAL (pooled) σ of the trend across stocks — NOT each stock's own σ. Own-σ
/// normalisation self-mutes a big mover exactly when it should read greed; pooled σ keeps "up vs the market"
/// legible. Taker-FLOW is de-emphasised here because in this sim it is largely reversion flow (bots sell into
/// strength), so it points anti-trend. Vol is the fear intensity (a spike ⇒ fear).
///
/// Threading: the loop thread owns every write (Tick/Observe/RecordTakerFlow/Score); the HTTP thread only reads
/// the cached <see cref="MoodFor"/>. Per-stock state keys are populated once at construction, so a lock-free
/// TryGetValue is safe off-thread (mirrors BotActivityService and the existing MoodForStock read).
/// </summary>
// Public so DI can inject the singleton into the public ctors of AiTradeService + CandleService (an internal
// type in a public constructor signature is a CS0051 accessibility error). Members stay internal.
public sealed class MarketMoodService
{
    // ---- config (bound from Bots:Mood:* in the AiTradeService ctor) ----
    private readonly bool _enabled;
    private readonly MoodWeights _w;
    private readonly double _anchorTau, _volTau, _volBaselineTau, _flowTau, _smoothTau;

    // ---- per-stock state (mutable in place on the loop thread) ----
    private sealed class State
    {
        public double Price;        // last smoothed price
        public double PriceEma;     // EMA of price = the trend anchor (~AnchorTau)
        public double Trend;        // ln(Price / PriceEma): >0 above anchor (uptrend), <0 below (downtrend)
        public double VolEwma;      // EWMA of |ret| (fast, ~VolTau)
        public double VolBaseline;  // EWMA of |ret| (slow, ~VolBaselineTau)
        public double BuyEwma;      // EWMA of buy  taker notional (~FlowTau)
        public double SellEwma;     // EWMA of sell taker notional (~FlowTau)
        public double BuyPending;   // this-tick buy  taker notional, drained by Observe
        public double SellPending;  // this-tick sell taker notional, drained by Observe
        public bool   Seeded;       // first valid price seeds the anchor
        public long   Count;        // observations folded so far (drives the warmup guard)
        public double Score = 50.0; // last computed 0..100 (read off-thread)
    }

    private readonly Dictionary<int, State> _state;

    private double _dt = 1.0;       // seconds since the last mood tick (drives the EWMA keeps)
    private DateTime _lastTick;
    private bool _haveLastTick;

    // Ticks a stock must be observed before its composite is trusted — the first handful of samples leave the
    // anchor / vol baseline unstable, so the gauge reports neutral (50) until then.
    private const long WarmupObs = 20;

    // DI singleton: reads all Bots:Mood:* config + the stock universe itself so the same instance can be
    // injected into both AiTradeService (writer) and CandleService (flush-time reader) with no DI cycle.
    public MarketMoodService(IStockService stocks, IConfiguration config)
    {
        _enabled = config.GetValue("Bots:Mood:Enabled", false);
        _w = new MoodWeights(
            Mom:     config.GetValue("Bots:Mood:WMom", 0.9),
            Breadth: config.GetValue("Bots:Mood:WBreadth", 0.35),
            Vol:     config.GetValue("Bots:Mood:WVol", 0.2),
            Flow:    config.GetValue("Bots:Mood:WFlow", 0.15),
            Sent:    config.GetValue("Bots:Mood:WSent", 0.2));
        _anchorTau      = Math.Max(1.0, config.GetValue("Bots:Mood:AnchorTauSec", 600.0));
        _volTau         = Math.Max(1.0, config.GetValue("Bots:Mood:VolTauSec", 60.0));
        _volBaselineTau = Math.Max(1.0, config.GetValue("Bots:Mood:VolBaselineTauSec", 900.0));
        _flowTau        = Math.Max(1.0, config.GetValue("Bots:Mood:FlowTauSec", 300.0));
        _smoothTau      = Math.Max(0.0, config.GetValue("Bots:Mood:SmoothTauSec", 60.0));   // 0 = no output smoothing

        _state = new Dictionary<int, State>();
        foreach (var sid in stocks.ById.Keys) _state[sid] = new State();
    }

    /// <summary>Gate for the live composite path. When false the endpoint uses the v1 sentiment×activity fallback.</summary>
    public bool Enabled => _enabled;

    /// <summary>Loop thread: advance the internal clock so this tick's EWMA keeps reflect the real elapsed time.</summary>
    public void Tick(DateTime now)
    {
        if (_haveLastTick)
            _dt = Math.Clamp((now - _lastTick).TotalSeconds, 0.05, 10.0);
        _lastTick = now;
        _haveLastTick = true;
    }

    private static double Keep(double dt, double tau) => Math.Exp(-dt / tau);

    /// <summary>
    /// Loop thread: buffer one taker (aggressor) fill's signed notional. Called from the batch-apply loop where
    /// the new/aggressing order side (<c>IsBuyOrder</c>) is the taker; the resting counterparty is not in the
    /// batch, so signing by the new order's side is genuine taker-flow imbalance. Drained by the next Observe.
    /// </summary>
    public void RecordTakerFlow(int stockId, bool isBuy, decimal notional)
    {
        if (notional <= 0m || !_state.TryGetValue(stockId, out var s)) return;
        double n = (double)notional;
        if (isBuy) s.BuyPending += n; else s.SellPending += n;
    }

    /// <summary>
    /// Loop thread: fold this tick's smoothed price into the per-stock state — advance the trend anchor, the
    /// return-derived vol EWMAs, and the buffered taker flow.
    /// </summary>
    public void Observe(int stockId, double price)
    {
        if (price <= 0.0 || !_state.TryGetValue(stockId, out var s)) return;
        if (!s.Seeded)
        {
            s.Price = price; s.PriceEma = price; s.Trend = 0.0; s.Seeded = true; s.Count++;
            return;
        }

        double ret = (price - s.Price) / s.Price;
        double absr = Math.Abs(ret);
        if (s.VolBaseline <= 0.0) { s.VolEwma = absr; s.VolBaseline = absr; }   // seed vol on the first real return

        double kAnc  = Keep(_dt, _anchorTau);
        double kVol  = Keep(_dt, _volTau);
        double kVolB = Keep(_dt, _volBaselineTau);
        double kFlow = Keep(_dt, _flowTau);

        s.PriceEma    = kAnc  * s.PriceEma    + (1 - kAnc)  * price;
        s.Trend       = Math.Log(price / Math.Max(1e-12, s.PriceEma));   // +uptrend / −downtrend
        s.VolEwma     = kVol  * s.VolEwma     + (1 - kVol)  * absr;
        s.VolBaseline = kVolB * s.VolBaseline + (1 - kVolB) * absr;
        s.BuyEwma     = kFlow * s.BuyEwma     + (1 - kFlow) * s.BuyPending;
        s.SellEwma    = kFlow * s.SellEwma    + (1 - kFlow) * s.SellPending;
        s.BuyPending = 0.0;
        s.SellPending = 0.0;
        s.Price = price;
        s.Count++;
    }

    /// <summary>Fraction of warmed stocks in an uptrend (trend &gt; 0) — breadth, 0..1. One scan.</summary>
    public double ComputeBreadth()
    {
        int up = 0, n = 0;
        foreach (var s in _state.Values) { if (s.Count < WarmupObs) continue; if (s.Trend > 0) up++; n++; }
        return n > 0 ? (double)up / n : 0.5;
    }

    /// <summary>Cross-sectional (pooled) σ of the trend across warmed stocks — the scale that z-scores each
    /// stock's trend against "how big a trend the typical name has right now". Floored to avoid a blow-up.</summary>
    public double ComputePooledSigma()
    {
        double sum = 0, sumsq = 0; int n = 0;
        foreach (var s in _state.Values) { if (s.Count < WarmupObs) continue; sum += s.Trend; sumsq += s.Trend * s.Trend; n++; }
        if (n < 2) return 1e-6;
        double mean = sum / n;
        double var = Math.Max(0.0, sumsq / n - mean * mean);
        return Math.Max(1e-6, Math.Sqrt(var));
    }

    /// <summary>
    /// Loop thread: derive the normalised signals and cache this stock's 0..100 composite mood. <paramref name="breadth"/>
    /// is the market-wide up-fraction, <paramref name="pooledSigma"/> the cross-sectional trend scale, and
    /// <paramref name="sentiment"/> the stock's slow anchor.
    /// </summary>
    public double Score(int stockId, double breadth, double pooledSigma, double sentiment)
    {
        if (!_state.TryGetValue(stockId, out var s)) return 50.0;
        if (s.Count < WarmupObs) { s.Score = 50.0; return 50.0; }   // not enough samples ⇒ report neutral

        // Trend-vs-anchor direction, normalised by the pooled cross-sectional σ (not own-σ), winsorized.
        double momZ = pooledSigma > 1e-12 ? Math.Clamp(s.Trend / pooledSigma, -3.0, 3.0) : 0.0;
        // Vol relative to its own slow baseline (a spike ⇒ fear). Winsorized so a lagging baseline can't blow up.
        double volZ = s.VolBaseline > 1e-12 ? Math.Clamp(s.VolEwma / s.VolBaseline - 1.0, -0.9, 3.0) : 0.0;
        double denom = s.BuyEwma + s.SellEwma;
        double flowZ = denom > 1e-12 ? (s.BuyEwma - s.SellEwma) / denom : 0.0;

        double raw = MoodScore(_w, momZ, breadth, volZ, flowZ, sentiment);
        // Output smoothing: EMA the reported score so the dial doesn't lurch when a mean-reverting stock
        // crosses its trend anchor tick-to-tick. Preserves the direction (a slow EMA of a correct signal is
        // still correct), just damps the jitter. 0 tau ⇒ raw (no smoothing).
        double kS = _smoothTau > 0.0 ? Keep(_dt, _smoothTau) : 0.0;
        s.Score = kS * s.Score + (1 - kS) * raw;
        return s.Score;
    }

    /// <summary>Off-thread read: the last cached composite score for a stock, or 50 if unknown.</summary>
    public double MoodFor(int stockId) => _state.TryGetValue(stockId, out var s) ? s.Score : 50.0;

    /// <summary>Loop-thread snapshot of the mood distribution for the periodic soak log (mean + range + 5 buckets).</summary>
    public (double mean, double min, double max, int[] hist) Distribution()
    {
        var hist = new int[5];
        double sum = 0, mn = 100, mx = 0; int n = 0;
        foreach (var s in _state.Values)
        {
            double v = s.Score; sum += v; n++;
            if (v < mn) mn = v; if (v > mx) mx = v;
            hist[Math.Clamp((int)(v / 20), 0, 4)]++;
        }
        return n > 0 ? (sum / n, mn, mx, hist) : (50.0, 50.0, 50.0, hist);
    }

    // ---- the pure composite (unit-tested) ----
    // mood = 50 + 50·tanh( wMom·momZ + wBreadth·(2·breadth−1) − wVol·volZ + wFlow·flowZ + wSent·sentiment )
    // momZ = trend-vs-anchor z-score (direction, leads); vol SPIKE (volZ > 0) subtracts → fear; broad rally
    // (breadth > 0.5) and buy taker pressure (flowZ > 0) add → greed; sentiment is a small slow anchor.
    internal static double MoodScore(in MoodWeights w, double momZ, double breadth, double volZ, double flowZ, double sentiment)
        => Math.Clamp(
               50.0 + 50.0 * Math.Tanh(
                     w.Mom     * momZ
                   + w.Breadth * (2.0 * breadth - 1.0)
                   - w.Vol     * volZ
                   + w.Flow    * flowZ
                   + w.Sent    * sentiment),
               0.0, 100.0);

    // v1 fallback — byte-identical to the pre-composite endpoint. Used when Bots:Mood:Enabled=false so the live
    // gauge keeps working (truthful sentiment×activity) until the composite is wired and soak-validated.
    internal static double LegacyMoodScore(double sentiment, double activity, double greedScale)
        => Math.Clamp(50.0 + 50.0 * Math.Tanh(greedScale * sentiment * Math.Max(0.0, activity)), 0.0, 100.0);
}

/// <summary>
/// The five composite weights (bound from <c>Bots:Mood:W*</c>). Trend-vs-anchor momentum leads; taker flow is
/// de-emphasised (reversion flow in this sim); vol is the fear intensity; sentiment a small slow anchor.
/// </summary>
internal readonly record struct MoodWeights(double Mom, double Breadth, double Vol, double Flow, double Sent);
