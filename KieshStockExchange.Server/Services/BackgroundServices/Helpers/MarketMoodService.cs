using System;
using System.Collections.Generic;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §fear-greed: a composite Fear/Greed index (0 = extreme fear, 50 = neutral, 100 = extreme greed) for the
/// bot market. This is the FAST layer of the one-axis-three-timescales model:
///   sentiment (slow OU anchor) → F&amp;G composite (this) → activity / taker (behavioural expression).
/// Unlike v1 (a smooth <c>sentiment × activity</c> proxy, which read "too smooth") the score is dominated by
/// FAST price-derived signals — momentum, breadth, realised-vol, taker-flow — so it feels alive; sentiment is
/// demoted to a small slow anchor. It is a READ-ONLY downstream projection: never fed back into the bot
/// decision accumulator as a SOURCE (the reflexive lever, when it lands, rides a separate lagged taker channel).
///
/// Threading: the loop thread owns every write (<see cref="Tick"/>/<see cref="Observe"/>/<see cref="RecordTakerFlow"/>/
/// <see cref="Score"/>); the HTTP thread only reads the cached <see cref="MoodFor"/>. Per-stock state keys are
/// populated once at construction, so a lock-free TryGetValue is safe off-thread (mirrors BotActivityService and
/// the existing MoodForStock read).
/// </summary>
internal sealed class MarketMoodService
{
    // ---- config (bound from Bots:Mood:* in the AiTradeService ctor) ----
    private readonly bool _enabled;
    private readonly MoodWeights _w;
    private readonly double _momTau, _momSigmaTau, _volTau, _volBaselineTau, _flowTau;

    // ---- per-stock EWMA state (mutable in place on the loop thread) ----
    private sealed class State
    {
        public double MomEwma;      // EWMA of per-tick return (recent drift, ~MomTau)
        public double MomVarEwma;   // EWMA of ret^2 (slow) → the sigma that z-scores momentum
        public double VolEwma;      // EWMA of |ret| (fast, ~VolTau)
        public double VolBaseline;  // EWMA of |ret| (slow, ~VolBaselineTau)
        public double BuyEwma;      // EWMA of buy  taker notional (~FlowTau)
        public double SellEwma;     // EWMA of sell taker notional (~FlowTau)
        public double BuyPending;   // this-tick buy  taker notional, drained by Observe
        public double SellPending;  // this-tick sell taker notional, drained by Observe
        public bool   Seeded;       // first Observe seeds the vol baselines (no cold-start z spike)
        public long   Count;        // observations folded so far (drives the warmup guard)
        public double Score = 50.0; // last computed 0..100 (read off-thread)
    }

    private readonly Dictionary<int, State> _state;

    private double _dt = 1.0;       // seconds since the last mood tick (drives the EWMA keeps)
    private DateTime _lastTick;
    private bool _haveLastTick;

    public MarketMoodService(
        IEnumerable<int> stockIds,
        bool enabled,
        MoodWeights weights,
        double momTauSec, double momSigmaTauSec, double volTauSec, double volBaselineTauSec, double flowTauSec)
    {
        _enabled        = enabled;
        _w              = weights;
        _momTau         = Math.Max(1.0, momTauSec);
        _momSigmaTau    = Math.Max(1.0, momSigmaTauSec);
        _volTau         = Math.Max(1.0, volTauSec);
        _volBaselineTau = Math.Max(1.0, volBaselineTauSec);
        _flowTau        = Math.Max(1.0, flowTauSec);

        _state = new Dictionary<int, State>();
        foreach (var sid in stockIds) _state[sid] = new State();
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
    /// Loop thread: fold this tick's return + buffered taker flow into the per-stock EWMA state. Vol is derived
    /// from |ret| internally so the caller only supplies the return the loop already computes.
    /// </summary>
    public void Observe(int stockId, double ret)
    {
        if (!_state.TryGetValue(stockId, out var s)) return;
        double absr = Math.Abs(ret);
        if (!s.Seeded)
        {
            s.MomVarEwma = ret * ret;
            s.VolEwma = absr;
            s.VolBaseline = absr;
            s.Seeded = true;
        }

        double kMom  = Keep(_dt, _momTau);
        double kSig  = Keep(_dt, _momSigmaTau);
        double kVol  = Keep(_dt, _volTau);
        double kVolB = Keep(_dt, _volBaselineTau);
        double kFlow = Keep(_dt, _flowTau);

        s.MomEwma     = kMom  * s.MomEwma     + (1 - kMom)  * ret;
        s.MomVarEwma  = kSig  * s.MomVarEwma  + (1 - kSig)  * ret * ret;
        s.VolEwma     = kVol  * s.VolEwma     + (1 - kVol)  * absr;
        s.VolBaseline = kVolB * s.VolBaseline + (1 - kVolB) * absr;
        s.BuyEwma     = kFlow * s.BuyEwma     + (1 - kFlow) * s.BuyPending;
        s.SellEwma    = kFlow * s.SellEwma    + (1 - kFlow) * s.SellPending;
        s.BuyPending = 0.0;
        s.SellPending = 0.0;
        s.Count++;
    }

    // Ticks a stock must be observed before its composite is trusted — the first handful of samples leave the
    // momentum σ / vol baseline unstable, so the gauge reports neutral (50) until then.
    private const long WarmupObs = 20;

    /// <summary>Fraction of tracked stocks whose smoothed recent return is positive (breadth, 0..1). One scan.</summary>
    public double ComputeBreadth()
    {
        int up = 0, n = 0;
        foreach (var s in _state.Values) { if (s.MomEwma > 0) up++; n++; }
        return n > 0 ? (double)up / n : 0.5;
    }

    /// <summary>
    /// Loop thread: derive the normalised signals from state and cache this stock's 0..100 composite mood.
    /// <paramref name="breadth"/> is the market-wide up-fraction; <paramref name="sentiment"/> the stock's slow anchor.
    /// </summary>
    public double Score(int stockId, double breadth, double sentiment)
    {
        if (!_state.TryGetValue(stockId, out var s)) return 50.0;
        if (s.Count < WarmupObs) { s.Score = 50.0; return 50.0; }   // not enough samples ⇒ report neutral

        double momSigma = Math.Sqrt(Math.Max(0.0, s.MomVarEwma));
        // Winsorize the z-scores. During warmup (or a spike) the fast EWMA can outrun a still-settling σ/baseline;
        // clamping keeps each term to a sane contribution so no single signal pegs the tanh on cold-start noise.
        double momZ = momSigma > 1e-12 ? Math.Clamp(s.MomEwma / momSigma, -3.0, 3.0) : 0.0;
        double volZ = s.VolBaseline > 1e-12 ? Math.Clamp(s.VolEwma / s.VolBaseline - 1.0, -0.9, 3.0) : 0.0;
        double denom = s.BuyEwma + s.SellEwma;
        double flowZ = denom > 1e-12 ? (s.BuyEwma - s.SellEwma) / denom : 0.0;

        double score = MoodScore(_w, momZ, breadth, volZ, flowZ, sentiment);
        s.Score = score;
        return score;
    }

    /// <summary>Off-thread read: the last cached composite score for a stock, or 50 if unknown.</summary>
    public double MoodFor(int stockId) => _state.TryGetValue(stockId, out var s) ? s.Score : 50.0;

    /// <summary>Loop-thread snapshot of the mood distribution for the periodic soak log (global mean + 5 buckets).</summary>
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
    // Fast price-derived terms dominate (momentum leads); sentiment is a small slow anchor. A vol SPIKE
    // (volZ > 0) is subtracted → fear; buy pressure (flowZ > 0) and a broad rally (breadth > 0.5) → greed.
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
/// The five composite weights (bound from <c>Bots:Mood:W*</c>). Momentum dominant; sentiment a small slow anchor.
/// </summary>
internal readonly record struct MoodWeights(double Mom, double Breadth, double Vol, double Flow, double Sent);
