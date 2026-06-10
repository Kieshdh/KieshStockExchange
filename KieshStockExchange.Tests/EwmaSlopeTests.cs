using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// Sentiment-dynamics §: the per-stock sentiment slope is a two-timescale EWMA of raw=(s_now−s_prev)/dt
/// (BotSentimentService.EwmaSlope). These cover sign (rising→+, falling→−, flat→0), the τ-based smoothing
/// (larger τ reacts slower / smooths a spike more), and frame-rate behaviour, mirroring the pure-static
/// test style used for CashHomeostasis.
/// </summary>
public class EwmaSlopeTests
{
    private const double Dt = 1.0, TauFast = 45.0, TauSlow = 180.0;

    // Drive a constant per-tick step `step` for `n` ticks from ds=0 and return the EWMA slope.
    private static double Drive(double step, int n, double dt, double tau)
    {
        double ds = 0.0, s = 0.0;
        for (int i = 0; i < n; i++)
        {
            double sPrev = s;
            s += step;
            ds = BotSentimentService.EwmaSlope(ds, s, sPrev, dt, tau);
        }
        return ds;
    }

    [Fact]
    public void Rising_series_has_positive_slope_falling_negative()
    {
        Assert.True(Drive(+0.05, 30, Dt, TauFast) > 0.0);
        Assert.True(Drive(-0.05, 30, Dt, TauFast) < 0.0);
    }

    [Fact]
    public void Flat_series_decays_slope_toward_zero()
    {
        // Build up a slope, then feed a long flat run — the EWMA must relax back toward 0.
        double ds = 0.0, s = 0.0;
        for (int i = 0; i < 30; i++) { double p = s; s += 0.05; ds = BotSentimentService.EwmaSlope(ds, s, p, Dt, TauFast); }
        for (int i = 0; i < 600; i++) ds = BotSentimentService.EwmaSlope(ds, s, s, Dt, TauFast); // raw=0 each step
        Assert.True(System.Math.Abs(ds) < 1e-3, $"slope should decay to ~0, got {ds}");
    }

    [Fact]
    public void Steady_slope_converges_toward_the_raw_rate()
    {
        // A sustained constant step of 0.02/tick at dt=1 ⇒ raw=0.02; a long EWMA converges to ~raw.
        double ds = Drive(0.02, 4000, Dt, TauFast);
        Assert.InRange(ds, 0.018, 0.02);
    }

    [Fact]
    public void Larger_tau_smooths_a_step_more_slowly()
    {
        // Same input; the slower (larger τ) EWMA lags more, so its slope estimate is smaller after few ticks.
        double fast = Drive(0.05, 10, Dt, TauFast);
        double slow = Drive(0.05, 10, Dt, TauSlow);
        Assert.True(fast > slow, $"fast τ should track quicker: fast={fast}, slow={slow}");
    }

    [Fact]
    public void Nonpositive_dt_is_a_no_op()
    {
        Assert.Equal(0.123, BotSentimentService.EwmaSlope(0.123, 5.0, 1.0, 0.0, TauFast));
    }
}
