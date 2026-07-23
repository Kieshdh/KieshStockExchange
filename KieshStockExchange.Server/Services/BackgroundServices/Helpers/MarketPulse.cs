using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// §market-pulse: a universal, reusable per-stock "momentum rhythm" oscillator. Makes bot-driven price moves look more
/// NATURAL / random-walk-like: during a directional move (news / high sentiment / regime push) the driving TAKER-flow rate
/// BREATHES — loud (fast leg up) → quiet (momentum slows / pauses) → loud (leg up again) — instead of a uniform high-intensity
/// glide (Kiesh's "$500 → $502, slow, then up again"). Each USE-SITE constructs its own instance with its own oscillation
/// time (τ) and amplitude (A), so the same primitive can drive taker-rate now and spread/news later, each tuned independently.
///
/// Math (per stock <c>s</c>, stepped once per tick):
///   τ_s = TauMinSec + (TauMaxSec−TauMinSec)·hash(s)      // per-stock jitter de-phases the fleet (no metronomic lockstep)
///   α   = exp(−Δt / τ_s)                                 // dt-invariant OU decay
///   z_s ← clamp( α·z_s + √(1−α²)·σ_z·U(−1,1)·√3 , −1, 1 ) // OU on z∈[−1,1]; √3 makes U(−1,1) unit-variance
///   Mult = exp( A·z_s − ½·A²·σ_z² )                       // LOG-symmetric (×M up ↔ ÷M down) + MEAN-CORRECTED ⇒ E[Mult]≈1
///
/// The mean-correction makes it provably VARIANCE-not-mean: the pulse re-shapes momentum WITHIN a move but cannot drift the
/// average taker rate (so it never secretly pushes price up or down — the wash-out / no-net-bias guarantee). σ_z sizes the
/// tail: 0.60 keeps |z|→1 (the strong bursts/lulls) a RARE tail, not a wall-pinned square wave.
///
/// <b>Disabled ⇒ byte-identical:</b> when <c>Enabled=false</c> the RNG is never advanced, <c>z</c> is never touched, and
/// <see cref="Mult"/> returns exactly 1.0 — no RNG-stream divergence ⇒ CK byte-identical. Dedicated RNG stream (seeded off
/// the caller's salt) so enabling it does not perturb any other per-tick draw. Loop-thread only (no locking).
/// </summary>
internal sealed class MarketPulse
{
    private const double Sqrt3 = 1.7320508075688772;   // U(−1,1)·√3 = unit variance
    private const double MinTauSec = 1.0;

    private readonly bool   _enabled;
    private readonly double _amplitude;   // A — log-symmetric magnitude (per use-site)
    private readonly double _sigmaZ;      // OU stationary sd (tail source)
    private readonly double _tauMinSec;   // per-stock τ range (per use-site)
    private readonly double _tauMaxSec;
    private readonly int    _salt;        // per-use-site salt: distinct τ-phase + RNG stream per channel

    private readonly Dictionary<int, double> _z = new();
    private Random _rng;

    /// <param name="enabled">master gate; false ⇒ inert + byte-identical.</param>
    /// <param name="amplitude">A, the log-symmetric multiplier magnitude (0 ⇒ Mult≡1).</param>
    /// <param name="sigmaZ">OU stationary sd (≈0.60 = rare-tail bursts).</param>
    /// <param name="tauMinSec">/<paramref name="tauMaxSec"/> per-stock oscillation-period range (seconds).</param>
    /// <param name="rngSeed">base seed; XORed with <paramref name="salt"/> for this instance's dedicated stream.</param>
    /// <param name="salt">per-use-site salt (distinct channels get distinct τ-phase + RNG).</param>
    internal MarketPulse(bool enabled, double amplitude, double sigmaZ,
                         double tauMinSec, double tauMaxSec, int rngSeed, int salt)
    {
        _amplitude = Math.Max(0.0, amplitude);
        _enabled   = enabled && _amplitude > 0.0;
        _sigmaZ    = Math.Max(0.0, sigmaZ);
        _tauMinSec = Math.Max(MinTauSec, tauMinSec);
        _tauMaxSec = Math.Max(_tauMinSec, tauMaxSec);
        _salt      = salt;
        _rng       = new Random(rngSeed ^ salt);
    }

    /// <summary>True when this instance actually oscillates (enabled + amplitude &gt; 0).</summary>
    internal bool Active => _enabled;

    /// <summary>Clear per-stock state and reseed the dedicated stream. Call from the owner's Reset.</summary>
    internal void Reset(int rngSeed)
    {
        _z.Clear();
        _rng = new Random(rngSeed ^ _salt);
    }

    /// <summary>
    /// Advance stock <paramref name="stockId"/>'s OU one tick (Δt = <paramref name="dtSec"/>). No-op + no RNG draw when
    /// disabled ⇒ byte-identical. Call once per stock per tick from the owner's per-stock loop, BEFORE reading <see cref="Mult"/>.
    /// </summary>
    internal void Step(int stockId, double dtSec)
    {
        if (!_enabled) return;
        double tau = _tauMinSec + (_tauMaxSec - _tauMinSec) * BotMath.HashUnit01(stockId ^ _salt);
        double a   = Math.Exp(-Math.Max(0.0, dtSec) / tau);
        double z   = a * (_z.TryGetValue(stockId, out var pv) ? pv : 0.0)
                     + Math.Sqrt(Math.Max(0.0, 1.0 - a * a)) * _sigmaZ * (_rng.NextDouble() * 2.0 - 1.0) * Sqrt3;
        _z[stockId] = Math.Clamp(z, -1.0, 1.0);
    }

    /// <summary>
    /// The log-symmetric, mean-corrected multiplier for stock <paramref name="stockId"/> (E[Mult]≈1). Exactly 1.0 when
    /// disabled or before the first <see cref="Step"/>. Multiply a positive RATE/SIZE scalar (e.g. taker strength) by this.
    /// </summary>
    internal double Mult(int stockId)
    {
        if (!_enabled) return 1.0;
        double z = _z.TryGetValue(stockId, out var v) ? v : 0.0;
        return Math.Exp(_amplitude * z - 0.5 * _amplitude * _amplitude * _sigmaZ * _sigmaZ);
    }
}
