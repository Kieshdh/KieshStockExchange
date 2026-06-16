using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// Realism #2 (price→sentiment contrarian feedback). Covers the signed dead-band
/// (BotSentimentService.Deadband) and the contrarian-reaction shape used in Tick:
/// reaction(cum) = clamp(−strength·Deadband(cum, band), −cap, +cap), driven by a leaky-integrated
/// recent return. Pure-static / replicated-formula style, mirroring EwmaSlopeTests.
/// </summary>
public class SentimentPriceReactionTests
{
    // ---- Dead-band ----
    [Fact]
    public void Deadband_off_when_band_nonpositive() => Assert.Equal(0.05, BotSentimentService.Deadband(0.05, 0.0));

    [Fact]
    public void Deadband_zeroes_within_band()
    {
        Assert.Equal(0.0, BotSentimentService.Deadband(0.008, 0.01));
        Assert.Equal(0.0, BotSentimentService.Deadband(-0.01, 0.01)); // at the edge ⇒ zero
    }

    [Fact]
    public void Deadband_passes_signed_excess_beyond()
    {
        Assert.Equal(0.02, BotSentimentService.Deadband(0.03, 0.01), 12);
        Assert.Equal(-0.02, BotSentimentService.Deadband(-0.03, 0.01), 12);
    }

    // ---- Contrarian reaction shape (replicates the Tick one-liner) ----
    private static double Reaction(double cum, double strength, double band, double cap)
        => System.Math.Clamp(-strength * BotSentimentService.Deadband(cum, band), -cap, cap);

    [Fact]
    public void Sustained_up_move_pushes_sentiment_down_and_vice_versa()
    {
        Assert.True(Reaction(+0.05, 6.0, 0.01, 0.40) < 0.0); // up move ⇒ negative (contrarian)
        Assert.True(Reaction(-0.05, 6.0, 0.01, 0.40) > 0.0); // down move ⇒ positive
    }

    [Fact]
    public void Small_move_within_band_yields_no_reaction()
        => Assert.Equal(0.0, Reaction(0.008, 6.0, 0.01, 0.40)); // protects the 1-min scale (ret_acf_lag1)

    [Fact]
    public void Reaction_is_clamped_to_cap()
        => Assert.Equal(-0.40, Reaction(0.50, 6.0, 0.01, 0.40)); // huge move ⇒ pinned at −cap

    // ---- Leaky-integrated recent return is tick-rate-stable (≈ move over τ) ----
    // cum_t = keep·cum_{t-1} + r ; steady state for constant per-tick r ⇒ r/(1−keep) ≈ (r/dt)·τ.
    // ---- #3 fast momentum term (same-sign, clamped) ----
    private static double Momentum(double cumFast, double strength, double cap)
        => System.Math.Clamp(strength * cumFast, -cap, cap);

    [Fact]
    public void Momentum_pushes_same_direction_as_the_move()
    {
        Assert.True(Momentum(+0.02, 5.0, 0.25) > 0.0); // up move ⇒ positive (chase)
        Assert.True(Momentum(-0.02, 5.0, 0.25) < 0.0);
    }

    [Fact]
    public void Momentum_is_clamped_to_cap()
        => Assert.Equal(0.25, Momentum(0.10, 5.0, 0.25)); // 5*0.10=0.5 ⇒ pinned at +cap

    [Fact]
    public void Leaky_integral_of_returns_converges_to_window_move()
    {
        const double dt = 1.0, tau = 300.0, rPerTick = 0.0001; // 0.01%/tick
        double keep = System.Math.Exp(-dt / tau), cum = 0.0;
        for (int i = 0; i < 5000; i++) cum = keep * cum + rPerTick;
        // ≈ (r/dt)·τ = 0.0001·300 = 0.03 (a ~3% move over the 5-min window).
        Assert.InRange(cum, 0.029, 0.031);
    }
}
