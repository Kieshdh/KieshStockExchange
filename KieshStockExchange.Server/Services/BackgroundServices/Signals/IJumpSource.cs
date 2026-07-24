using System.Linq;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// A signed, per-stock price-JUMP intent to realize this tick. <see cref="SignedTargetPct"/> is the target
/// realized close-to-close move as a fraction of the current mark (e.g. +0.04 ⇒ a +4% one-bucket jump). The
/// sign is the desired push direction; the unit both the jump state machine and every jump source share.
/// </summary>
internal readonly record struct JumpEvent(int StockId, double SignedTargetPct);

/// <summary>
/// The "what jumps arrive and when" seam, kept separate from the "how a jump is realized" orchestrator
/// (<see cref="JumpService"/>) — mirrors the <see cref="IShockSource"/>/<see cref="ExogenousShockService"/>
/// split. The random Poisson generator is the only implementation today; a scripted source (earnings,
/// macro events) could implement the same interface untouched. <see cref="Poll"/> is keyed on a monotonic
/// <c>simTick</c> (NOT wall-clock) so any scheduled content stays deterministic/replayable.
/// </summary>
internal interface IJumpSource
{
    /// <summary>Jump intents arriving this tick. Called once per loop iteration, only while the feature is enabled.</summary>
    IEnumerable<JumpEvent> Poll(long simTick, double dt);

    /// <summary>Re-arm to a deterministic initial state (reseed RNG) at session start.</summary>
    void Reset();
}

/// <summary>
/// Random fat-tail jump source: an independent, RARE Poisson arrival per stock with a power-law target
/// magnitude. Arrival is RATE-based (<c>1 − exp(−dt/mean)</c>) so the event rate is loop-frequency
/// independent, and the draw order is fixed (arrival → sign → magnitude, over a stable stock iteration) so
/// runs reproduce — identical discipline to <see cref="RandomShockSource"/>. Dedicated RNG drawn ONLY inside
/// <see cref="Poll"/> (which the service calls only when enabled) ⇒ off path untouched / byte-identical.
/// </summary>
internal sealed class RandomJumpSource : IJumpSource
{
    // Dedicated seed, distinct from every existing seeded stream: sentiment 43 (+derived 10753/23671),
    // fx 47, regime 53, activity 59, fundamental 71, exog-shock 89. 97 is the next free prime.
    private const int RngSeed = 97;

    private readonly IStockService _stocks;
    private readonly double _meanIntervalSec;
    private readonly double _minPct;
    private readonly double _maxPct;
    private readonly double _exp;

    private Random _rng = new(RngSeed);

    internal RandomJumpSource(IStockService stocks, double meanIntervalHours,
        double minPct, double maxPct, double magnitudeExponent)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _meanIntervalSec = Math.Max(0.01, meanIntervalHours) * 3600.0; // floor avoids div-by-zero
        _minPct = Math.Max(0.0, minPct);
        _maxPct = Math.Max(_minPct, maxPct);
        _exp    = Math.Max(1.0, magnitudeExponent);
    }

    public void Reset() => _rng = new Random(RngSeed);

    public IEnumerable<JumpEvent> Poll(long simTick, double dt)
    {
        // Rate-based arrival probability per stock for the elapsed (clamped) dt — loop-rate independent.
        double p = 1.0 - Math.Exp(-dt / _meanIntervalSec);
        List<JumpEvent>? events = null;
        foreach (var sid in _stocks.ById.Keys) // stable iteration order ⇒ reproducible draw sequence
        {
            if (_rng.NextDouble() >= p) continue;               // draw 1: arrival
            double sign = _rng.NextDouble() < 0.5 ? -1.0 : 1.0; // draw 2: sign
            double mag  = BotMath.DrawMagnitude(_rng, _minPct, _maxPct, _exp); // draw 3: magnitude
            (events ??= new List<JumpEvent>()).Add(new JumpEvent(sid, sign * mag));
        }
        return events ?? Enumerable.Empty<JumpEvent>();
    }
}
