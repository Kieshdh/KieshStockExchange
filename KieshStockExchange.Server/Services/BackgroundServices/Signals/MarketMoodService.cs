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
/// §per-timeframe (council): the SAME emotional axis is rated at THREE horizon BANDS — Fast (index 0, the
/// shipped 15s-5m gauge), Mid (1), Slow (2). Each band keeps its own trend anchor EMA, vol EWMAs, output
/// smoothing, and a weight-multiplier; the price series, taker-flow EWMAs, and sentiment are SHARED per stock,
/// while breadth and pooled-σ are computed PER BAND. The Fast band uses the existing top-level <c>Bots:Mood:*</c>
/// keys unchanged, so it is byte-identical to the single-band build: <see cref="MoodFor"/>, persistence, and
/// the reflexive lever all read Fast and are unaffected.
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
    // §per-timeframe: band indices. Fast (0) = the shipped gauge; Mid (1) / Slow (2) rate the slower trend.
    public const int BandFast = 0, BandMid = 1, BandSlow = 2, BandCount = 3;

    // ---- config (bound from Bots:Mood:* + Bots:Mood:Bands:* in the ctor) ----
    private readonly bool _enabled;
    private readonly MoodWeights[] _bandW;                                       // pre-scaled weights per band
    private readonly double[] _anchorTau, _volTau, _volBaselineTau, _smoothTau;  // length BandCount
    private readonly double _flowTau, _globalEmaTau;                             // shared across bands

    // §reflexive-coupling: the LAGGED market-wide mood (5-min EMA of the global mean), read pre-pick by the
    // reflexive taker lever. The lag is load-bearing — it low-passes the mood→taker→price→mood loop to ~0 at
    // tick frequency so reflexivity only acts at regime timescale. A runtime kill-switch (no restart) trips it off.
    private double _laggedGlobal = 50.0;
    internal static volatile bool ReflexiveKillSwitch = false;

    // §mood-latch telemetry: a cheap rolling counter of how often the lagged global mood sits in an EXTREME band
    // (<30 fear / >70 greed) since the last log drain — a fear-spiral / latch indicator for the soak. Loop-thread
    // only (bumped in UpdateLaggedGlobal, drained by the periodic MOOD log).
    private long _latchTicks, _totalLatchTicks;

    // ---- per-stock state (mutable in place on the loop thread) ----
    // Per-band derived state: each band has its own trend anchor + vol EWMAs + smoothed score.
    private sealed class Band
    {
        public double PriceEma;     // EMA of price = the trend anchor (~AnchorTau[band])
        public double Trend;        // ln(Price / PriceEma): >0 above anchor (uptrend), <0 below (downtrend)
        public double VolEwma;      // EWMA of |ret| (fast, ~VolTau[band])
        public double VolBaseline;  // EWMA of |ret| (slow, ~VolBaselineTau[band])
        public double Score = 50.0; // last computed 0..100 (read off-thread)
    }

    private sealed class State
    {
        public double Price;        // last smoothed price (shared across bands)
        public double BuyEwma;      // EWMA of buy  taker notional (~FlowTau, shared)
        public double SellEwma;     // EWMA of sell taker notional (~FlowTau, shared)
        public double BuyPending;   // this-tick buy  taker notional, drained by Observe
        public double SellPending;  // this-tick sell taker notional, drained by Observe
        public bool   Seeded;       // first valid price seeds the anchors
        public long   Count;        // observations folded so far (drives the warmup guard)
        public readonly Band[] Bands = { new Band(), new Band(), new Band() };
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
        var wFast = new MoodWeights(   // ×1.5 the validated baseline = the swingier 'Medium' index (council-tuned)
            Mom:     config.GetValue("Bots:Mood:WMom", 1.35),
            Breadth: config.GetValue("Bots:Mood:WBreadth", 0.5),
            Vol:     config.GetValue("Bots:Mood:WVol", 0.3),
            Flow:    config.GetValue("Bots:Mood:WFlow", 0.2),
            Sent:    config.GetValue("Bots:Mood:WSent", 0.3));
        // Mid/Slow reuse the Fast weight SHAPE, scaled down (WeightMult<1 = a calmer long-horizon dial).
        double midMult  = config.GetValue("Bots:Mood:Bands:Mid:WeightMult", 0.8);
        double slowMult = config.GetValue("Bots:Mood:Bands:Slow:WeightMult", 0.67);
        _bandW = new[] { wFast, Scale(wFast, midMult), Scale(wFast, slowMult) };
        // Fast uses the top-level keys (unchanged); Mid/Slow read Bots:Mood:Bands:{Mid,Slow}:*.
        _anchorTau = new[] {
            Math.Max(1.0, config.GetValue("Bots:Mood:AnchorTauSec", 600.0)),
            Math.Max(1.0, config.GetValue("Bots:Mood:Bands:Mid:AnchorTauSec", 7200.0)),
            Math.Max(1.0, config.GetValue("Bots:Mood:Bands:Slow:AnchorTauSec", 43200.0)) };
        _volTau = new[] {
            Math.Max(1.0, config.GetValue("Bots:Mood:VolTauSec", 60.0)),
            Math.Max(1.0, config.GetValue("Bots:Mood:Bands:Mid:VolTauSec", 600.0)),
            Math.Max(1.0, config.GetValue("Bots:Mood:Bands:Slow:VolTauSec", 3600.0)) };
        _volBaselineTau = new[] {
            Math.Max(1.0, config.GetValue("Bots:Mood:VolBaselineTauSec", 900.0)),
            Math.Max(1.0, config.GetValue("Bots:Mood:Bands:Mid:VolBaselineTauSec", 10800.0)),
            Math.Max(1.0, config.GetValue("Bots:Mood:Bands:Slow:VolBaselineTauSec", 86400.0)) };
        _smoothTau = new[] {
            Math.Max(0.0, config.GetValue("Bots:Mood:SmoothTauSec", 45.0)),
            Math.Max(0.0, config.GetValue("Bots:Mood:Bands:Mid:SmoothTauSec", 300.0)),
            Math.Max(0.0, config.GetValue("Bots:Mood:Bands:Slow:SmoothTauSec", 1800.0)) };
        _flowTau      = Math.Max(1.0, config.GetValue("Bots:Mood:FlowTauSec", 300.0));
        _globalEmaTau = Math.Max(1.0, config.GetValue("Bots:Mood:MoodEmaSeconds", 300.0)); // lag for the reflexive lever

        _state = new Dictionary<int, State>();
        foreach (var sid in stocks.ById.Keys) _state[sid] = new State();
    }

    private static MoodWeights Scale(in MoodWeights w, double m) => new(w.Mom * m, w.Breadth * m, w.Vol * m, w.Flow * m, w.Sent * m);

    /// <summary>Gate for the live composite path. When false the endpoint uses the v1 sentiment×activity fallback.</summary>
    public bool Enabled => _enabled;

    /// <summary>§per-timeframe: which band a candle of this base/target resolution should DISPLAY.
    /// ≤5m ⇒ Fast, ≤1h ⇒ Mid, else Slow.</summary>
    public static int BandForBucket(int bucketSeconds) => bucketSeconds <= 300 ? BandFast : bucketSeconds <= 3600 ? BandMid : BandSlow;

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
    /// Loop thread: fold this tick's smoothed price into the per-stock state — advance EACH band's trend anchor
    /// and return-derived vol EWMAs (shared price/return), plus the shared buffered taker flow.
    /// </summary>
    public void Observe(int stockId, double price)
    {
        if (price <= 0.0 || !_state.TryGetValue(stockId, out var s)) return;
        if (!s.Seeded)
        {
            s.Price = price;
            foreach (var b in s.Bands) { b.PriceEma = price; b.Trend = 0.0; }
            s.Seeded = true; s.Count++;
            return;
        }

        double ret = (price - s.Price) / s.Price;
        double absr = Math.Abs(ret);
        double kFlow = Keep(_dt, _flowTau);

        for (int i = 0; i < BandCount; i++)
        {
            var b = s.Bands[i];
            if (b.VolBaseline <= 0.0) { b.VolEwma = absr; b.VolBaseline = absr; }   // seed vol on the first real return
            double kAnc  = Keep(_dt, _anchorTau[i]);
            double kVol  = Keep(_dt, _volTau[i]);
            double kVolB = Keep(_dt, _volBaselineTau[i]);
            b.PriceEma    = kAnc  * b.PriceEma    + (1 - kAnc)  * price;
            b.Trend       = Math.Log(price / Math.Max(1e-12, b.PriceEma));   // +uptrend / −downtrend
            b.VolEwma     = kVol  * b.VolEwma     + (1 - kVol)  * absr;
            b.VolBaseline = kVolB * b.VolBaseline + (1 - kVolB) * absr;
        }

        s.BuyEwma  = kFlow * s.BuyEwma  + (1 - kFlow) * s.BuyPending;
        s.SellEwma = kFlow * s.SellEwma + (1 - kFlow) * s.SellPending;
        s.BuyPending = 0.0;
        s.SellPending = 0.0;
        s.Price = price;
        s.Count++;
    }

    /// <summary>Fraction of warmed stocks in an uptrend (band trend &gt; 0) — breadth, 0..1. One scan.</summary>
    public double ComputeBreadth(int band)
    {
        int up = 0, n = 0;
        foreach (var s in _state.Values) { if (s.Count < WarmupObs) continue; if (s.Bands[band].Trend > 0) up++; n++; }
        return n > 0 ? (double)up / n : 0.5;
    }

    /// <summary>Cross-sectional (pooled) σ of the band's trend across warmed stocks — the scale that z-scores each
    /// stock's trend against "how big a trend the typical name has right now". Floored to avoid a blow-up.</summary>
    public double ComputePooledSigma(int band)
    {
        double sum = 0, sumsq = 0; int n = 0;
        foreach (var s in _state.Values)
        {
            if (s.Count < WarmupObs) continue;
            var t = s.Bands[band].Trend;
            sum += t; sumsq += t * t; n++;
        }
        if (n < 2) return 1e-6;
        double mean = sum / n;
        double var = Math.Max(0.0, sumsq / n - mean * mean);
        return Math.Max(1e-6, Math.Sqrt(var));
    }

    /// <summary>
    /// Loop thread: derive the normalised signals and cache this stock's 0..100 composite mood FOR ONE BAND.
    /// <paramref name="breadth"/> is the band's market-wide up-fraction, <paramref name="pooledSigma"/> the band's
    /// cross-sectional trend scale, and <paramref name="sentiment"/> the stock's slow anchor (shared across bands).
    /// </summary>
    public double Score(int stockId, int band, double breadth, double pooledSigma, double sentiment)
    {
        if (!_state.TryGetValue(stockId, out var s)) return 50.0;
        var b = s.Bands[band];
        if (s.Count < WarmupObs) { b.Score = 50.0; return 50.0; }   // not enough samples ⇒ report neutral

        // Trend-vs-anchor direction, normalised by the pooled cross-sectional σ (not own-σ), winsorized.
        double momZ = pooledSigma > 1e-12 ? Math.Clamp(b.Trend / pooledSigma, -3.0, 3.0) : 0.0;
        // Vol relative to its own slow baseline (a spike ⇒ fear). Winsorized so a lagging baseline can't blow up.
        double volZ = b.VolBaseline > 1e-12 ? Math.Clamp(b.VolEwma / b.VolBaseline - 1.0, -0.9, 3.0) : 0.0;
        double denom = s.BuyEwma + s.SellEwma;
        double flowZ = denom > 1e-12 ? (s.BuyEwma - s.SellEwma) / denom : 0.0;

        double raw = MoodScore(_bandW[band], momZ, breadth, volZ, flowZ, sentiment);
        // Output smoothing: EMA the reported score so the dial doesn't lurch when a mean-reverting stock
        // crosses its trend anchor tick-to-tick. Preserves the direction (a slow EMA of a correct signal is
        // still correct), just damps the jitter. 0 tau ⇒ raw (no smoothing).
        double kS = _smoothTau[band] > 0.0 ? Keep(_dt, _smoothTau[band]) : 0.0;
        b.Score = kS * b.Score + (1 - kS) * raw;
        return b.Score;
    }

    /// <summary>Off-thread read: the last cached Fast-band score for a stock, or 50 if unknown.</summary>
    public double MoodFor(int stockId) => _state.TryGetValue(stockId, out var s) ? s.Bands[BandFast].Score : 50.0;

    /// <summary>Off-thread read: the last cached score for a stock ON A SPECIFIC BAND, or 50 if unknown.</summary>
    public double MoodForBand(int stockId, int band) => _state.TryGetValue(stockId, out var s) ? s.Bands[band].Score : 50.0;

    /// <summary>Loop thread: fold the current global mean mood into the lagged (5-min EMA) global mood that the
    /// reflexive taker lever reads. Called once per tick after all stocks are scored. Uses the Fast band.</summary>
    public void UpdateLaggedGlobal()
    {
        var (mean, _, _, _) = Distribution(BandFast);
        double k = Keep(_dt, _globalEmaTau);
        _laggedGlobal = k * _laggedGlobal + (1 - k) * mean;
        _totalLatchTicks++;
        if (_laggedGlobal < 30.0 || _laggedGlobal > 70.0) _latchTicks++;   // §mood-latch: extreme-band dwell fraction
    }

    /// <summary>Loop-thread read: the lagged market-wide mood (0..100) for the reflexive taker lever.</summary>
    public double LaggedGlobalMood() => _laggedGlobal;

    /// <summary>§mood-latch telemetry: fraction of ticks since the last drain that the lagged global mood was in an
    /// extreme band (&lt;30 or &gt;70) — a latch/fear-spiral indicator for the periodic MOOD log. Resets on read.</summary>
    public double DrainLatchFraction()
    {
        double f = _totalLatchTicks > 0 ? (double)_latchTicks / _totalLatchTicks : 0.0;
        _latchTicks = 0; _totalLatchTicks = 0;
        return f;
    }

    /// <summary>Loop-thread snapshot of a band's mood distribution for the periodic soak log (mean + range + 5 buckets).</summary>
    public (double mean, double min, double max, int[] hist) Distribution(int band = BandFast)
    {
        var hist = new int[5];
        double sum = 0, mn = 100, mx = 0; int n = 0;
        foreach (var s in _state.Values)
        {
            double v = s.Bands[band].Score; sum += v; n++;
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
