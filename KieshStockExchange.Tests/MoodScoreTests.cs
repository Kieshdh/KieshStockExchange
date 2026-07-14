using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §fear-greed: the pure composite math for <see cref="MarketMoodService"/>. Locks the neutral fixed-point (50),
/// per-term monotonicity + signs (momentum/breadth/flow greed-ward, vol fear-ward, sentiment a small anchor), the
/// [0,100] clamp, the weight-zero degeneracy, and tanh saturation — so a refactor can't silently drift the gauge.
/// </summary>
public class MoodScoreTests
{
    // Default weights (kept in sync with AiTradeService / appsettings Bots:Mood:W*).
    private static readonly MoodWeights W = new(Mom: 0.9, Breadth: 0.35, Vol: 0.2, Flow: 0.15, Sent: 0.2);

    private static double Score(double momZ, double breadth, double volZ, double flowZ, double sent)
        => MarketMoodService.MoodScore(W, momZ, breadth, volZ, flowZ, sent);

    #region Neutral fixed point + range
    [Fact]
    public void Neutral_inputs_score_exactly_fifty()
    {
        // momZ 0, breadth 0.5 (2·0.5−1=0), volZ 0, flowZ 0, sentiment 0 ⇒ tanh(0)=0 ⇒ 50.
        Assert.Equal(50.0, Score(0, 0.5, 0, 0, 0), 12);
    }

    [Fact]
    public void Output_is_always_in_zero_to_hundred()
    {
        double[] grid = { -1000, -5, -1, -0.2, 0, 0.2, 1, 5, 1000 };
        foreach (var m in grid)
            foreach (var b in new[] { 0.0, 0.5, 1.0 })
                foreach (var v in grid)
                    foreach (var f in new[] { -1.0, 0.0, 1.0 })
                        foreach (var s in new[] { -3.0, 0.0, 3.0 })
                            Assert.InRange(Score(m, b, v, f, s), 0.0, 100.0);
    }
    #endregion

    #region Per-term monotonicity + signs
    [Fact]
    public void Momentum_pushes_toward_greed_monotonically()
    {
        Assert.True(Score(1, 0.5, 0, 0, 0) > 50.0);   // positive momentum ⇒ greed
        Assert.True(Score(-1, 0.5, 0, 0, 0) < 50.0);  // negative ⇒ fear
        Assert.True(Score(2, 0.5, 0, 0, 0) > Score(1, 0.5, 0, 0, 0)); // more ⇒ greedier
    }

    [Fact]
    public void Breadth_above_half_is_greed_below_is_fear()
    {
        Assert.True(Score(0, 1.0, 0, 0, 0) > 50.0);   // whole market up
        Assert.True(Score(0, 0.0, 0, 0, 0) < 50.0);   // whole market down
        Assert.True(Score(0, 0.8, 0, 0, 0) > Score(0, 0.6, 0, 0, 0));
    }

    [Fact]
    public void Volatility_is_inverted_a_spike_is_fear()
    {
        Assert.True(Score(0, 0.5, 1, 0, 0) < 50.0);   // vol above baseline ⇒ fear
        Assert.True(Score(0, 0.5, -0.5, 0, 0) > 50.0); // unusually calm ⇒ greed
        Assert.True(Score(0, 0.5, 2, 0, 0) < Score(0, 0.5, 1, 0, 0)); // bigger spike ⇒ more fear
    }

    [Fact]
    public void Flow_buy_imbalance_is_greed_sell_is_fear()
    {
        Assert.True(Score(0, 0.5, 0, 1, 0) > 50.0);   // net buy taker flow ⇒ greed
        Assert.True(Score(0, 0.5, 0, -1, 0) < 50.0);  // net sell ⇒ fear
    }

    [Fact]
    public void Sentiment_is_a_small_anchor_not_a_driver()
    {
        Assert.True(Score(0, 0.5, 0, 0, 1) > 50.0);   // positive sentiment nudges up
        Assert.True(Score(0, 0.5, 0, 0, -1) < 50.0);
        // ...but momentum (weight 1.0) dominates sentiment (weight 0.25): a strong up-momentum still reads greed
        // even against bearish sentiment.
        Assert.True(Score(1.5, 0.5, 0, 0, -1) > 50.0);
    }
    #endregion

    #region Degeneracy + saturation
    [Fact]
    public void All_zero_weights_pin_the_gauge_at_fifty()
    {
        var zero = new MoodWeights(0, 0, 0, 0, 0);
        Assert.Equal(50.0, MarketMoodService.MoodScore(zero, 5, 1.0, -3, 1, 3), 12);
        Assert.Equal(50.0, MarketMoodService.MoodScore(zero, -9, 0.0, 4, -1, -3), 12);
    }

    [Fact]
    public void Extreme_inputs_saturate_toward_but_never_past_the_bounds()
    {
        double hi = Score(50, 1.0, -50, 1, 3);
        double lo = Score(-50, 0.0, 50, -1, -3);
        Assert.True(hi > 99.99 && hi <= 100.0);
        Assert.True(lo < 0.01 && lo >= 0.0);
    }

    [Fact]
    public void Score_is_deterministic()
    {
        for (int i = 0; i < 200; i++)
        {
            double m = (i - 100) / 37.0, b = (i % 11) / 10.0, v = (i - 50) / 23.0;
            double f = ((i % 21) - 10) / 10.0, s = (i - 100) / 50.0;
            Assert.Equal(Score(m, b, v, f, s), Score(m, b, v, f, s), 12);
        }
    }
    #endregion

    #region v1 legacy fallback (endpoint stays working when the composite is off)
    [Fact]
    public void Legacy_neutral_is_fifty_and_sign_follows_sentiment()
    {
        Assert.Equal(50.0, MarketMoodService.LegacyMoodScore(0.0, 1.0, 1.2), 12);
        Assert.True(MarketMoodService.LegacyMoodScore(0.5, 1.0, 1.2) > 50.0);
        Assert.True(MarketMoodService.LegacyMoodScore(-0.5, 1.0, 1.2) < 50.0);
    }

    [Fact]
    public void Legacy_activity_is_a_nonnegative_intensity_gain_and_output_bounded()
    {
        // A busier market (higher activity) amplifies the same sentiment tilt.
        Assert.True(MarketMoodService.LegacyMoodScore(0.4, 3.0, 1.2) > MarketMoodService.LegacyMoodScore(0.4, 1.0, 1.2));
        // Negative activity is clamped to 0 (can't invert the sign) and the output stays bounded.
        Assert.Equal(50.0, MarketMoodService.LegacyMoodScore(0.9, -5.0, 1.2), 12);
        Assert.InRange(MarketMoodService.LegacyMoodScore(9.0, 9.0, 1.2), 0.0, 100.0);
    }
    #endregion
}
