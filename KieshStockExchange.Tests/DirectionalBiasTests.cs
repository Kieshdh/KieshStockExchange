using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// Sentiment-dynamics §: the slope-aware phase model (AiBotDecisionService.DirectionalBias) maps
/// (level s, fast/slow slope ds, per-bot lateness L) to a signed buyProb shift per strategy. These pin the
/// qualitative phase behaviour (Scalper follows ds, high-L FOMO chases the level, MeanReversion fades the
/// extreme and harder on the reversal, Random=0) plus the symmetry that keeps the engine drift-free.
/// </summary>
public class DirectionalBiasTests
{
    // Representative conviction weights + slope scales (the production defaults).
    private const decimal Km = 0.15m, Ks = 0.20m, Kr = 0.15m, Kr2 = 0.10m, Kmm = 0.05m;
    private const decimal Sf = 0.01m, Ss = 0.005m;

    private static decimal Bias(AiStrategy strat, decimal s, decimal dsF, decimal dsS, decimal L) =>
        AiBotDecisionService.DirectionalBias(strat, s, dsF, dsS, L, Km, Ks, Kr, Kr2, Kmm, Sf, Ss);

    [Fact]
    public void Scalper_follows_the_fast_slope_and_flips_on_reversal()
    {
        // Early (L=0) scalper, flat level: rising fast slope ⇒ buy, falling ⇒ sell.
        Assert.True(Bias(AiStrategy.Scalper, 0m, +0.05m, 0m, 0m) > 0m);
        Assert.True(Bias(AiStrategy.Scalper, 0m, -0.05m, 0m, 0m) < 0m);
    }

    [Fact]
    public void High_lateness_momentum_bot_chases_the_level()
    {
        // A high-L (FOMO) TrendFollower buys a high level EVEN as the slope has rolled over (ds_slow ≤ 0).
        var bias = Bias(AiStrategy.TrendFollower, s: 0.8m, dsF: 0m, dsS: -0.01m, L: 0.95m);
        Assert.True(bias > 0m, $"late money should chase the high level, got {bias}");
        // … whereas an early (L=0) TrendFollower at the same point is SELLING the rollover (follows ds<0).
        Assert.True(Bias(AiStrategy.TrendFollower, 0.8m, 0m, -0.01m, 0m) < 0m);
    }

    [Fact]
    public void MeanReversion_fades_the_extreme_and_harder_on_the_reversal()
    {
        // High level ⇒ sell (fade). Lateness is irrelevant for the reversion cohort.
        var fadeFlat   = Bias(AiStrategy.MeanReversion, s: 0.8m, dsF: 0m, dsS: 0m,      L: 0.5m);
        var fadeTurning = Bias(AiStrategy.MeanReversion, s: 0.8m, dsF: 0m, dsS: -0.02m, L: 0.5m);
        Assert.True(fadeFlat < 0m);
        Assert.True(fadeTurning < fadeFlat, "should fade harder once the extreme rolls over");
    }

    [Fact]
    public void Random_has_no_directional_bias()
    {
        Assert.Equal(0m, Bias(AiStrategy.Random, 0.9m, 0.05m, 0.05m, 0.9m));
    }

    [Fact]
    public void MarketMaker_leans_gently_against_the_level()
    {
        Assert.True(Bias(AiStrategy.MarketMaker, 0.6m, 0m, 0m, 0m) < 0m);
        Assert.True(Bias(AiStrategy.MarketMaker, -0.6m, 0m, 0m, 0m) > 0m);
    }

    [Theory]
    [InlineData(0.7, 0.03, 0.02, 0.2)]
    [InlineData(-0.4, -0.05, 0.01, 0.9)]
    [InlineData(0.9, -0.02, -0.04, 0.6)]
    public void Bias_is_symmetric_under_sign_flip(double s, double dsF, double dsS, double l)
    {
        foreach (var strat in new[] { AiStrategy.Scalper, AiStrategy.TrendFollower,
                                      AiStrategy.MeanReversion, AiStrategy.MarketMaker })
        {
            var pos = Bias(strat, (decimal)s, (decimal)dsF, (decimal)dsS, (decimal)l);
            var neg = Bias(strat, (decimal)-s, (decimal)-dsF, (decimal)-dsS, (decimal)l);
            Assert.True(System.Math.Abs(pos + neg) < 1e-9m,
                $"{strat} not symmetric: f(x)={pos}, f(-x)={neg}");
        }
    }
}
