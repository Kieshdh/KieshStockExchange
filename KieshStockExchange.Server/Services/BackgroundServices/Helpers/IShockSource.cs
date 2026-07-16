using System.Linq;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// A signed, per-stock fundamental-value innovation to apply this tick. Magnitude is a fraction of seed
/// (e.g. +0.04 = a +4% news impulse). The unit both the shock state machine and every shock source share.
/// <para>§news-permanence: <paramref name="PermanentFraction"/> (α∈[0.30,0.90]) is the share of this impulse that
/// enters the shock's PERMANENT residual floor (the rest is transient overshoot); <paramref name="DecayHalfLifeSec"/>
/// (τ½) is the transient's per-event decay half-life. Both default to <c>0.0</c> = the LEGACY SENTINEL: no residual,
/// decay the whole impulse at the service's global half-life (byte-identical to the pre-permanence engine).</para>
/// </summary>
internal readonly record struct ShockImpulse(int StockId, double SignedMagnitude,
    double PermanentFraction = 0.0, double DecayHalfLifeSec = 0.0);

/// <summary>Which fan-out tier an event belongs to — selects its (AlphaShift, TauMult, LambdaAfter) means on the SAME draw.</summary>
internal enum ShockTier { Individual, Sector, Global }

/// <summary>
/// §news-permanence config carrier (spec docs/NEWS_PERMANENCE_COUPLING.md §2). All fields inert at their defaults;
/// <see cref="Enabled"/>=false ⇒ the permanence RNG is never constructed, impulses carry the α=0/τ=0 sentinel, and
/// the shock state machine is byte-identical to today's decay-to-zero. Kept as a plain carrier so the (α,τ) coupling
/// draw is a pure static and the whole block wires from config in one place.
/// </summary>
internal sealed class NewsPermanenceOptions
{
    public bool   Enabled;                      // master gate; false ⇒ byte-identical (no RNG, sentinel impulses)
    public double AlphaMin = 0.30;              // permanent-fraction floor
    public double AlphaMax = 0.90;              // permanent-fraction ceiling
    public double AlphaSpread = 0.40;          // σ of n1 — de-rigs the α↔τ line
    public double TauMedianSec = 1500.0;        // transient half-life median
    public double TauSpread = 0.40;            // σ of n2
    public double TauMinSec = 300.0;
    public double TauMaxSec = 2400.0;
    public double Coupling = 0.6;               // β: NEGATIVE α↔lnτ coupling; 0 ⇒ independent draws
    public double ResidualHalfLifeSec = 10800.0; // permanent floor slow bleed (consumed by the service)
    public int    PermRngSeed;                  // 0 ⇒ derive as RngSeed ^ 0x3B

    // Aftershocks (Lambda=0 ⇒ inert)
    public double AfterLambda = 0.6;
    public double AfterDelayMedianSec = 300.0;
    public double AfterDelaySpread = 0.6;
    public double AfterMagFracMin = 0.3;
    public double AfterMagFracMax = 0.7;
    public double AfterSameSignProb = 0.7;
    public double AfterDecay = 0.5;
    public int    AfterMaxDepth = 1;            // 1 ⇒ NO aftershocks-of-aftershocks

    // Per-tier means on the base draw
    public (double AlphaShift, double TauMult, double LambdaAfter) Individual = (0.00, 1.0, 0.6);
    public (double AlphaShift, double TauMult, double LambdaAfter) Sector     = (-0.10, 1.6, 0.4);
    public (double AlphaShift, double TauMult, double LambdaAfter) Global     = (-0.22, 2.4, 0.3);

    public (double AlphaShift, double TauMult, double LambdaAfter) Tier(ShockTier t) => t switch
    {
        ShockTier.Sector => Sector,
        ShockTier.Global => Global,
        _                => Individual,
    };
}

/// <summary>
/// The "what shocks arrive and when" seam, kept separate from the "how shocks decay/clamp/are read" state
/// machine (<see cref="ExogenousShockService"/>). The random Poisson generator is the only implementation
/// today; a future scripted source (earnings calendar, macro events, sector rotation, user narratives)
/// implements the same interface with ZERO edits to decay/clamp/anchor/chaser. <see cref="Poll"/> is keyed
/// on a monotonic <c>simTick</c> (NOT wall-clock) so any scheduled content stays deterministic/replayable.
/// </summary>
internal interface IShockSource
{
    /// <summary>Impulses arriving this tick. Called once per loop iteration, only while the feature is enabled.</summary>
    IEnumerable<ShockImpulse> Poll(long simTick, double dt);

    /// <summary>Re-arm to a deterministic initial state (reseed RNG / rewind cursor) at session start.</summary>
    void Reset();

    /// <summary>The shared sign (±1) of a MARKET-WIDE (global) impulse emitted in the most recent <see cref="Poll"/>,
    /// or 0 if none fired this tick. Drives the global co-fire burst (all co-firers act same-tick, same-sign). A
    /// source with no global stream returns 0 always ⇒ co-fire inert.</summary>
    int LastGlobalSign { get; }

    /// <summary>The sector (0..SectorCount−1) a global pulse was scoped to in the most recent <see cref="Poll"/>,
    /// or −1 for a market-wide pulse / no pulse. Restricts the co-fire cohort to one sector ⇒ intra-sector flow. A
    /// source with no sector scoping returns −1 always ⇒ sector filtering inert.</summary>
    int LastGlobalSector { get; }
}

/// <summary>
/// Random exogenous-news source: an independent Poisson arrival per stock with a power-law magnitude. Arrival
/// is RATE-based (<c>1 − exp(−dt/mean)</c>) so the event rate is independent of the loop frequency, and the
/// draw order is fixed (arrival → sign → magnitude, over a stable stock iteration) so runs reproduce. Dedicated
/// RNG drawn ONLY inside <see cref="Poll"/> (which the service calls only when enabled) ⇒ off path untouched.
/// </summary>
internal sealed class RandomShockSource : IShockSource
{
    // Deterministic seed, distinct from sentiment(43)/fx(47)/regime(53)/fundamental(71).
    private const int RngSeed = 89;
    // §global-exog: a SEPARATE market-wide stream on its own RNG so GlobalFraction=0 never draws it ⇒ the
    // per-stock stream (and its RNG) is untouched = byte-identical off.
    private const int GlobalRngSeed = RngSeed ^ 0x1D;

    private readonly IStockService _stocks;
    private readonly double _meanIntervalSec;
    private readonly double _minMag;
    private readonly double _maxMag;
    private readonly double _exp;
    private readonly double _globalFraction; // 0 ⇒ per-stock-only (byte-identical); >0 ⇒ a shared market-wide stream
    private readonly int _sectorCount;       // 1 ⇒ no sectors (byte-identical); >1 ⇒ a global pulse may scope to one sector
    private readonly double _sectorFraction; // 0 ⇒ every global pulse market-wide (byte-identical); else the fraction scoped to a sector

    // §sector-exog: dedicated RNG, drawn ONLY when a global pulse fires AND sectors are enabled ⇒ the global stream stays untouched off.
    private const int SectorRngSeed = GlobalRngSeed ^ 0x2C;
    // §news-permanence: DEDICATED stream, constructed + drawn ONLY when permanence is enabled, so the per-stock /
    // global / sector RNG streams above stay byte-identical mid-migration (same pattern as GlobalRngSeed).
    private const int PermRngSeedDerive = RngSeed ^ 0x3B;

    private Random _rng = new(RngSeed);
    private Random _globalRng = new(GlobalRngSeed);
    private Random _sectorRng = new(SectorRngSeed);
    private int _lastGlobalSign; // ±1 when a global impulse fired this Poll, else 0 — the co-fire signal.
    private int _lastGlobalSector = -1; // 0..N−1 = a global pulse scoped to that sector; −1 = market-wide / none.

    // §news-permanence state. _permRng is null (and never drawn) when disabled ⇒ byte-identical off.
    private readonly NewsPermanenceOptions _perm;
    private readonly bool _permEnabled;
    private Random? _permRng;
    private double _simTimeSec; // accumulated sim seconds (Σ dt) — the aftershock timer-wheel clock.
    // Pending aftershocks: a full mini-event scheduled to fire at FireAtSec (its own (α,τ) is drawn ON fire). MaxDepth
    // is enforced by NEVER scheduling from an aftershock (only base arrivals schedule) ⇒ no grandchildren, non-branching.
    private readonly struct Pending { public Pending(int s, double f, double m, ShockTier t) { StockId = s; FireAtSec = f; SignedMag = m; Tier = t; } public readonly int StockId; public readonly double FireAtSec; public readonly double SignedMag; public readonly ShockTier Tier; }
    private readonly List<Pending> _pending = new();
    // Liveness counters (soak telemetry + test hooks). Base = drawn arrivals that could seed aftershocks; After = emitted follow-ups.
    internal long BaseEventCount { get; private set; }
    internal long AftershockCount { get; private set; }

    internal RandomShockSource(IStockService stocks, double meanIntervalMinutes,
        double minMagnitude, double maxMagnitude, double magnitudeExponent, double globalFraction = 0.0,
        int sectorCount = 1, double sectorFraction = 0.0, NewsPermanenceOptions? permanence = null)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _meanIntervalSec = Math.Max(0.01, meanIntervalMinutes) * 60.0; // floor avoids div-by-zero, allows fine calibration
        _minMag = Math.Max(0.0, minMagnitude);
        _maxMag = Math.Max(_minMag, maxMagnitude);
        _exp    = Math.Max(1.0, magnitudeExponent);
        _globalFraction = Math.Clamp(globalFraction, 0.0, 1.0);
        _sectorCount = Math.Max(1, sectorCount);
        _sectorFraction = Math.Clamp(sectorFraction, 0.0, 1.0);
        _perm = permanence ?? new NewsPermanenceOptions();
        _permEnabled = _perm.Enabled;
    }

    private int PermSeed => _perm.PermRngSeed != 0 ? _perm.PermRngSeed : PermRngSeedDerive;

    public void Reset()
    {
        _rng = new Random(RngSeed); _globalRng = new Random(GlobalRngSeed); _sectorRng = new Random(SectorRngSeed);
        _lastGlobalSign = 0; _lastGlobalSector = -1;
        // §news-permanence: construct the dedicated RNG ONLY when enabled — off leaves it null and never drawn.
        _permRng = _permEnabled ? new Random(PermSeed) : null;
        _simTimeSec = 0.0; _pending.Clear();
        BaseEventCount = 0; AftershockCount = 0;
    }

    /// <inheritdoc/>
    public int LastGlobalSign => _lastGlobalSign;

    /// <inheritdoc/>
    public int LastGlobalSector => _lastGlobalSector;

    public IEnumerable<ShockImpulse> Poll(long simTick, double dt)
    {
        _lastGlobalSign = 0; // cleared each Poll; set below only if the global stream fires this tick.
        _lastGlobalSector = -1; // cleared each Poll; set below only if a global pulse scopes to a sector.
        // Rate-based arrival probability per stock for the elapsed (clamped) dt — loop-rate independent.
        double p = 1.0 - Math.Exp(-dt / _meanIntervalSec);
        List<ShockImpulse>? impulses = null;

        // §news-permanence: advance the aftershock clock and emit any follow-ups due this tick FIRST (each draws its
        // own (α,τ) on fire). Entirely gated on _permEnabled ⇒ _permRng is null and none of this runs when off.
        if (_permEnabled)
        {
            _simTimeSec += dt;
            EmitDueAftershocks(ref impulses);
        }

        foreach (var sid in _stocks.ById.Keys) // stable iteration order ⇒ reproducible draw sequence
        {
            if (_rng.NextDouble() >= p) continue;          // draw 1: arrival
            double sign = _rng.NextDouble() < 0.5 ? -1.0 : 1.0; // draw 2: sign
            double mag  = BotMath.DrawMagnitude(_rng, _minMag, _maxMag, _exp); // draw 3: magnitude
            double signed = sign * mag;
            // §news-permanence: an INDEPENDENT (α,τ) per per-stock event (Individual tier), on the dedicated stream ⇒
            // the three legacy draws above are untouched. Off ⇒ sentinel (0,0) ⇒ decay-to-zero, byte-identical.
            var (alpha, tau) = DrawPermanence(ShockTier.Individual);
            (impulses ??= new List<ShockImpulse>()).Add(new ShockImpulse(sid, signed, alpha, tau));
            if (_permEnabled) { BaseEventCount++; ScheduleAftershocks(sid, signed, ShockTier.Individual); }
        }

        // §global-exog: a shared MARKET-WIDE impulse — ONE draw (shared sign + magnitude) applied to EVERY stock
        // this tick ⇒ correlated shock/flow (the cross-stock-correlation lever). Fires at the per-stock base rate
        // scaled by GlobalFraction. Dedicated RNG + skipped entirely at 0 ⇒ the per-stock stream above is untouched.
        if (_globalFraction > 0.0)
        {
            double pGlobal = p * _globalFraction;
            if (_globalRng.NextDouble() < pGlobal)                                       // draw 1: shared arrival
            {
                double sign = _globalRng.NextDouble() < 0.5 ? -1.0 : 1.0;                // draw 2: shared sign
                double mag  = BotMath.DrawMagnitude(_globalRng, _minMag, _maxMag, _exp); // draw 3: shared magnitude
                double signed = sign * mag;
                _lastGlobalSign = sign > 0.0 ? 1 : -1; // co-fire signal: the shared direction for this tick.
                // §sector pulse: a fraction of global pulses hit ONE sector only ⇒ intra-sector correlated flow. Off ⇒ −1 ⇒ market-wide.
                int sector = -1;
                if (_sectorCount > 1 && _sectorFraction > 0.0 && _sectorRng.NextDouble() < _sectorFraction)
                    sector = _sectorRng.Next(_sectorCount);
                _lastGlobalSector = sector;
                // §news-permanence: ONE shared (α,τ) for the whole cohort (Global tier market-wide, Sector tier scoped).
                var tier = sector >= 0 ? ShockTier.Sector : ShockTier.Global;
                var (alpha, tau) = DrawPermanence(tier);
                impulses ??= new List<ShockImpulse>();
                foreach (var sid in _stocks.ById.Keys)
                    if (sector < 0 || sid % _sectorCount == sector) // market-wide (−1) hits all; a sector pulse hits its sector only
                    {
                        impulses.Add(new ShockImpulse(sid, signed, alpha, tau));
                        if (_permEnabled) { BaseEventCount++; ScheduleAftershocks(sid, signed, tier); }
                    }
            }
        }

        return impulses ?? Enumerable.Empty<ShockImpulse>();
    }

    // §news-permanence coupling draw (spec §1.1): a single latent z jointly sets α (probit squash into
    // [AlphaMin,AlphaMax] + AlphaSpread·n1 jitter, +tier AlphaShift) and τ½ (log-linear exp(−β·z + TauSpread·n2)·TauMult,
    // clipped [TauMin,TauMax]). β>0 ⇒ NEGATIVE corr(α, lnτ): clean/high-z ⇒ high α + short τ. Returns the (0,0) sentinel
    // when permanence is off (no RNG drawn) ⇒ the impulse decays to zero at the global half-life = byte-identical.
    private (double Alpha, double Tau) DrawPermanence(ShockTier tier)
    {
        if (!_permEnabled) return (0.0, 0.0);
        var (aShift, tMult, _) = _perm.Tier(tier);
        return DrawAlphaTau(_permRng!, _perm, aShift, tMult);
    }

    /// <summary>Pure, RNG-explicit (α,τ) draw per spec §1.1 (§1.2 negative coupling) — draws z,n1,n2 then delegates to
    /// <see cref="ComputeAlphaTau"/>. Unit-testable in isolation.</summary>
    internal static (double Alpha, double Tau) DrawAlphaTau(Random rng, NewsPermanenceOptions o, double alphaShift, double tauMult)
    {
        double z  = BotMath.NextGaussian(rng);
        double n1 = BotMath.NextGaussian(rng);
        double n2 = BotMath.NextGaussian(rng);
        return ComputeAlphaTau(z, n1, n2, o, alphaShift, tauMult);
    }

    /// <summary>Pure (α,τ) map from an explicit latent + jitters (spec §1.1). α = probit squash of (z+AlphaSpread·n1)
    /// into [AlphaMin,AlphaMax] (+tier shift); τ = clip(TauMedian·exp(−Coupling·z + TauSpread·n2)·TauMult). NEGATIVE
    /// coupling: +z ⇒ higher α AND shorter τ. Fully deterministic ⇒ the coupling sign is unit-testable.</summary>
    internal static (double Alpha, double Tau) ComputeAlphaTau(double z, double n1, double n2,
        NewsPermanenceOptions o, double alphaShift, double tauMult)
    {
        double alpha = o.AlphaMin + (o.AlphaMax - o.AlphaMin) * BotMath.NormalCdf(z + o.AlphaSpread * n1);
        alpha = Math.Clamp(alpha + alphaShift, o.AlphaMin, o.AlphaMax);
        double tau = Math.Clamp(o.TauMedianSec * Math.Exp(-o.Coupling * z + o.TauSpread * n2) * tauMult, o.TauMinSec, o.TauMaxSec);
        return (alpha, tau);
    }

    // §news-permanence aftershocks (spec §2 Aftershock): on a BASE event, schedule Poisson(Lambda·tier LambdaAfter)
    // delayed follow-ups (lognormal delay, MagFrac of parent, SameSignProb, geometric Decay^k). Never called from an
    // emitted aftershock ⇒ MaxDepth=1 (no grandchildren), non-branching, bounded.
    private void ScheduleAftershocks(int stockId, double parentSignedMag, ShockTier tier)
    {
        if (_perm.AfterMaxDepth < 1) return;
        double lambda = _perm.AfterLambda * _perm.Tier(tier).LambdaAfter;
        int n = BotMath.SamplePoisson(_permRng!, lambda);
        for (int k = 1; k <= n; k++)
        {
            double delay   = _perm.AfterDelayMedianSec * Math.Exp(_perm.AfterDelaySpread * BotMath.NextGaussian(_permRng!));
            double magFrac = _perm.AfterMagFracMin + (_perm.AfterMagFracMax - _perm.AfterMagFracMin) * _permRng!.NextDouble();
            double signMul = _permRng!.NextDouble() < _perm.AfterSameSignProb ? 1.0 : -1.0; // same-sign dominance ⇒ emergent PEAD
            double childMag = parentSignedMag * magFrac * Math.Pow(_perm.AfterDecay, k) * signMul;
            _pending.Add(new Pending(stockId, _simTimeSec + delay, childMag, tier));
        }
    }

    // Emit + remove pending aftershocks whose fire time has arrived; each becomes a full mini-event with its OWN (α,τ).
    private void EmitDueAftershocks(ref List<ShockImpulse>? impulses)
    {
        if (_pending.Count == 0) return;
        for (int i = _pending.Count - 1; i >= 0; i--) // reverse so RemoveAt is O(1)-ish and index-stable
        {
            var pd = _pending[i];
            if (_simTimeSec < pd.FireAtSec) continue;
            var (alpha, tau) = DrawPermanence(pd.Tier);
            (impulses ??= new List<ShockImpulse>()).Add(new ShockImpulse(pd.StockId, pd.SignedMag, alpha, tau));
            AftershockCount++;
            _pending.RemoveAt(i);
        }
    }
}
