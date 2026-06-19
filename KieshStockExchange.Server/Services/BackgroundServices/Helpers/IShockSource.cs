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

    private readonly IStockService _stocks;
    private readonly double _meanIntervalSec;
    private readonly double _minMag;
    private readonly double _maxMag;
    private readonly double _exp;

    private Random _rng = new(RngSeed);

    internal RandomShockSource(IStockService stocks, double meanIntervalMinutes,
        double minMagnitude, double maxMagnitude, double magnitudeExponent)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _meanIntervalSec = Math.Max(0.01, meanIntervalMinutes) * 60.0; // floor avoids div-by-zero, allows fine calibration
        _minMag = Math.Max(0.0, minMagnitude);
        _maxMag = Math.Max(_minMag, maxMagnitude);
        _exp    = Math.Max(1.0, magnitudeExponent);
    }

    public void Reset() => _rng = new Random(RngSeed);

    public IEnumerable<ShockImpulse> Poll(long simTick, double dt)
    {
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
        return impulses ?? Enumerable.Empty<ShockImpulse>();
    }
}
