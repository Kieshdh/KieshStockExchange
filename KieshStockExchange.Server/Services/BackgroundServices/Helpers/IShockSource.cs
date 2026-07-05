using System.Linq;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// A signed, per-stock fundamental-value innovation to apply this tick. Magnitude is a fraction of seed
/// (e.g. +0.04 = a +4% news impulse). The unit both the shock state machine and every shock source share.
/// </summary>
internal readonly record struct ShockImpulse(int StockId, double SignedMagnitude);

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

    private Random _rng = new(RngSeed);
    private Random _globalRng = new(GlobalRngSeed);
    private int _lastGlobalSign; // ±1 when a global impulse fired this Poll, else 0 — the co-fire signal.

    internal RandomShockSource(IStockService stocks, double meanIntervalMinutes,
        double minMagnitude, double maxMagnitude, double magnitudeExponent, double globalFraction = 0.0)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _meanIntervalSec = Math.Max(0.01, meanIntervalMinutes) * 60.0; // floor avoids div-by-zero, allows fine calibration
        _minMag = Math.Max(0.0, minMagnitude);
        _maxMag = Math.Max(_minMag, maxMagnitude);
        _exp    = Math.Max(1.0, magnitudeExponent);
        _globalFraction = Math.Clamp(globalFraction, 0.0, 1.0);
    }

    public void Reset() { _rng = new Random(RngSeed); _globalRng = new Random(GlobalRngSeed); _lastGlobalSign = 0; }

    /// <inheritdoc/>
    public int LastGlobalSign => _lastGlobalSign;

    public IEnumerable<ShockImpulse> Poll(long simTick, double dt)
    {
        _lastGlobalSign = 0; // cleared each Poll; set below only if the global stream fires this tick.
        // Rate-based arrival probability per stock for the elapsed (clamped) dt — loop-rate independent.
        double p = 1.0 - Math.Exp(-dt / _meanIntervalSec);
        List<ShockImpulse>? impulses = null;
        foreach (var sid in _stocks.ById.Keys) // stable iteration order ⇒ reproducible draw sequence
        {
            if (_rng.NextDouble() >= p) continue;          // draw 1: arrival
            double sign = _rng.NextDouble() < 0.5 ? -1.0 : 1.0; // draw 2: sign
            double mag  = BotMath.DrawMagnitude(_rng, _minMag, _maxMag, _exp); // draw 3: magnitude
            (impulses ??= new List<ShockImpulse>()).Add(new ShockImpulse(sid, sign * mag));
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
                impulses ??= new List<ShockImpulse>();
                foreach (var sid in _stocks.ById.Keys) impulses.Add(new ShockImpulse(sid, signed));
            }
        }

        return impulses ?? Enumerable.Empty<ShockImpulse>();
    }
}
