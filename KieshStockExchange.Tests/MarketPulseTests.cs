using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §market-pulse: the universal per-stock OU "momentum rhythm" oscillator that breathes the regime-taker firing rate
/// so a directional move STEPS instead of gliding. The two invariants that make it prod-safe:
///   (1) DISABLED (or amplitude 0) ⇒ Mult≡1.0 with NO RNG draw ⇒ byte-identical / CK-safe off path.
///   (2) ENABLED ⇒ E[Mult]≈1 (log-symmetric + MEAN-CORRECTED) ⇒ it re-shapes momentum WITHIN a move but adds NO net
///       taker bias (never secretly pushes price up or down = the wash-out guarantee).
/// Direct construction (no disk I/O), mirroring the pure-helper style of SentimentSigmaMultTests.
/// </summary>
public class MarketPulseTests
{
    const int Seed = 43;
    const int Salt = 0x7A11;

    static MarketPulse Osc(bool enabled, double amplitude = 0.35, double sigmaZ = 0.60)
        => new(enabled, amplitude, sigmaZ, tauMinSec: 30.0, tauMaxSec: 90.0, rngSeed: Seed, salt: Salt);

    // ---- (1) off path = byte-identical ----
    [Fact]
    public void Disabled_mult_is_exactly_one_before_and_after_step()
    {
        var p = Osc(enabled: false);
        Assert.False(p.Active);
        Assert.Equal(1.0, p.Mult(7));           // before any Step
        for (int i = 0; i < 100; i++) p.Step(7, 1.0);
        Assert.Equal(1.0, p.Mult(7));           // Step is a no-op when disabled
    }

    [Fact]
    public void Amplitude_zero_is_inert_even_when_enabled()
    {
        var p = Osc(enabled: true, amplitude: 0.0);
        Assert.False(p.Active);                  // A=0 ⇒ treated as disabled
        p.Step(3, 1.0);
        Assert.Equal(1.0, p.Mult(3));
    }

    [Fact]
    public void Disabled_step_draws_no_rng_so_enabling_does_not_perturb_neighbours()
    {
        // A disabled instance must never advance its stream — proven by Mult staying exactly 1.0 no matter how many
        // steps run (any RNG draw would have moved z off 0). This is the "no RNG-stream divergence" guarantee.
        var p = Osc(enabled: false);
        for (int i = 0; i < 1000; i++) { p.Step(i % 5, 0.5); Assert.Equal(1.0, p.Mult(i % 5)); }
    }

    [Fact]
    public void Enabled_mult_at_rest_is_the_mean_correction_constant()
    {
        // At z=0 (before the first Step / right after Reset) an ENABLED pulse returns exp(−½A²σ²) < 1, NOT 1.0 — the
        // mean-correction floor. It's a harmless warm-up transient (z walks up to its σ-wide stationary band within a
        // few τ), and it's exactly why E[Mult] over the warmed fleet lands at 1 rather than above it.
        const double A = 0.35, SigZ = 0.60;
        double atRest = Math.Exp(-0.5 * A * A * SigZ * SigZ);
        var p = Osc(enabled: true, amplitude: A, sigmaZ: SigZ);
        Assert.Equal(atRest, p.Mult(11), 12);
        Assert.True(atRest < 1.0);
    }

    // ---- (2) mean-correction: E[Mult] ≈ 1 (no net bias) ----
    [Fact]
    public void Enabled_expected_mult_is_approximately_one_across_the_fleet()
    {
        var p = Osc(enabled: true);
        // Warm the OU to stationarity, then average Mult over many stocks × many ticks. Mean-correction makes
        // E[exp(A·z)] = exp(½A²σ²), cancelled by the −½A²σ² term ⇒ E[Mult]≈1. Sampling noise ⇒ loose tolerance.
        double sum = 0; int n = 0;
        for (int warm = 0; warm < 50; warm++) for (int s = 0; s < 50; s++) p.Step(s, 1.0);
        for (int t = 0; t < 400; t++)
        {
            for (int s = 0; s < 50; s++) { p.Step(s, 1.0); sum += p.Mult(s); n++; }
        }
        double mean = sum / n;
        Assert.InRange(mean, 0.95, 1.05);        // no persistent up/down bias
    }

    [Fact]
    public void Mult_stays_within_the_log_symmetric_envelope()
    {
        // z∈[−1,1] ⇒ Mult∈[exp(−A−½A²σ²), exp(A−½A²σ²)]. Never blows up or hits zero (bounded taker-rate breathing).
        const double A = 0.35, SigZ = 0.60;
        double lo = Math.Exp(-A - 0.5 * A * A * SigZ * SigZ);
        double hi = Math.Exp(A - 0.5 * A * A * SigZ * SigZ);
        var p = Osc(enabled: true, amplitude: A, sigmaZ: SigZ);
        for (int t = 0; t < 2000; t++) { p.Step(t % 7, 1.0); double m = p.Mult(t % 7); Assert.InRange(m, lo, hi); }
    }

    // ---- determinism + reset ----
    [Fact]
    public void Same_seed_and_salt_produce_identical_sequences()
    {
        var a = Osc(enabled: true);
        var b = Osc(enabled: true);
        for (int t = 0; t < 200; t++)
        {
            a.Step(t % 4, 1.0);
            b.Step(t % 4, 1.0);
            Assert.Equal(a.Mult(t % 4), b.Mult(t % 4));
        }
    }

    [Fact]
    public void Distinct_salts_decorrelate_the_two_channels()
    {
        var osc    = new MarketPulse(true, 0.35, 0.60, 30, 90, Seed, 0x7A11);
        var jitter = new MarketPulse(true, 0.35, 0.60, 30, 90, Seed, 0x3C0D);
        bool anyDifferent = false;
        for (int t = 0; t < 100; t++)
        {
            osc.Step(2, 1.0); jitter.Step(2, 1.0);
            if (osc.Mult(2) != jitter.Mult(2)) { anyDifferent = true; break; }
        }
        Assert.True(anyDifferent);               // different salt ⇒ different τ-phase + RNG stream
    }

    [Fact]
    public void Reset_reseeds_to_the_same_deterministic_sequence()
    {
        var p = Osc(enabled: true);
        for (int t = 0; t < 50; t++) p.Step(1, 1.0);
        double afterFirst = p.Mult(1);

        p.Reset(Seed);
        double atRest = Math.Exp(-0.5 * 0.35 * 0.35 * 0.60 * 0.60);
        Assert.Equal(atRest, p.Mult(1), 12);     // state cleared ⇒ z absent ⇒ back to the mean-correction floor
        for (int t = 0; t < 50; t++) p.Step(1, 1.0);
        Assert.Equal(afterFirst, p.Mult(1));     // same stream replays identically
    }
}
