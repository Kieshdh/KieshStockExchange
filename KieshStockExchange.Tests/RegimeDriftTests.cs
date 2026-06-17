using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// System A — RegimeDrift bounded random-walk step (BotSentimentService.RegimeStep). A cubic soft-wall keeps
/// the walk ~free near the middle (so it persists/trends) but pulls back hard near ±cap (so it can't run away),
/// with a final hard clamp. Pure-static, mirrors the SlowRingSigma / Deadband test style.
/// </summary>
public class RegimeDriftTests
{
    [Fact]
    public void Off_when_cap_nonpositive()
        => Assert.Equal(0.0, BotSentimentService.RegimeStep(0.3, 0.1, 0.0, 0.1));

    [Fact]
    public void Free_in_the_middle()
    {
        // near 0 the soft-wall is ≈0 ⇒ the increment passes through (this is the persistence/trend).
        Assert.Equal(0.02, BotSentimentService.RegimeStep(0.0, 0.02, 0.5, 0.1), 12);
        // at 10% of cap the soft-pull is negligible (-5e-5)
        Assert.InRange(BotSentimentService.RegimeStep(0.05, 0.0, 0.5, 0.1), 0.0499, 0.05);
    }

    [Fact]
    public void Pulls_back_near_the_cap()
    {
        // at the cap with no step: softPull = -k·cap ⇒ returns cap·(1-k)
        Assert.Equal(0.45, BotSentimentService.RegimeStep(0.5, 0.0, 0.5, 0.1), 12);
        Assert.Equal(-0.45, BotSentimentService.RegimeStep(-0.5, 0.0, 0.5, 0.1), 12); // symmetric
    }

    [Fact]
    public void Hard_clamps_to_cap()
    {
        Assert.Equal(0.5, BotSentimentService.RegimeStep(0.4, 10.0, 0.5, 0.1));    // huge up step ⇒ pinned at +cap
        Assert.Equal(-0.5, BotSentimentService.RegimeStep(-0.4, -10.0, 0.5, 0.1)); // pinned at -cap
    }
}
