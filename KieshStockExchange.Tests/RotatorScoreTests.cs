using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §rotator PIN 1: the ranking signal is 0.6·gap + 0.25·dir + 0.10·idio + 0.05·global, with the gap (the
/// price-vs-estimate deviation) the dominant term. These lock the weights so a can't-test refactor can't drift them.
/// </summary>
public class RotatorScoreTests
{
    [Fact]
    public void Weights_match_the_pin()
    {
        Assert.Equal(0.60, RotatorDecisionService.Score(1, 0, 0, 0), 10);
        Assert.Equal(0.25, RotatorDecisionService.Score(0, 1, 0, 0), 10);
        Assert.Equal(0.10, RotatorDecisionService.Score(0, 0, 1, 0), 10);
        Assert.Equal(0.05, RotatorDecisionService.Score(0, 0, 0, 1), 10);
        Assert.Equal(1.00, RotatorDecisionService.Score(1, 1, 1, 1), 10);
    }

    [Fact]
    public void Gap_dominates_the_ranking()
    {
        // A more-undervalued stock (bigger gap) outranks a less-undervalued one when the other terms are equal.
        double under = RotatorDecisionService.Score(0.08, 0.0, 0.0, 0.0);
        double fair  = RotatorDecisionService.Score(0.01, 0.0, 0.0, 0.0);
        Assert.True(under > fair);

        // The small idio term (already scaled to ±0.05 by the service) can't flip a clear gap ordering.
        double underWithBadIdio = RotatorDecisionService.Score(0.08, 0.0, -0.05, 0.0);
        Assert.True(underWithBadIdio > fair);
    }

    [Fact]
    public void RankWeightedPick_singleton_returns_zero()
    {
        Assert.Equal(0, RotatorDecisionService.RankWeightedPick(1, 0.0));
        Assert.Equal(0, RotatorDecisionService.RankWeightedPick(1, 0.9));
        Assert.Equal(0, RotatorDecisionService.RankWeightedPick(0, 0.5)); // degenerate ⇒ 0, no throw
    }

    [Fact]
    public void RankWeightedPick_stays_in_range_and_favours_the_top()
    {
        // Boundary hashes: lowest ⇒ best rank, top ⇒ last rank.
        Assert.Equal(0, RotatorDecisionService.RankWeightedPick(5, 0.0));
        Assert.Equal(4, RotatorDecisionService.RankWeightedPick(5, 0.999999));

        // Always in [0, halfLen) across the whole hash range.
        for (int h = 0; h <= 100; h++)
            Assert.InRange(RotatorDecisionService.RankWeightedPick(7, h / 100.0), 0, 6);

        // Triangular weighting: rank 0 is picked far more than the last rank over a uniform hash sweep.
        int pick0 = 0, pickLast = 0, n = 1000, len = 6;
        for (int i = 0; i < n; i++)
        {
            int idx = RotatorDecisionService.RankWeightedPick(len, i / (double)n);
            if (idx == 0) pick0++;
            if (idx == len - 1) pickLast++;
        }
        Assert.True(pick0 > pickLast * 3, $"rank 0 ({pick0}) should dominate the last rank ({pickLast})");
    }
}
