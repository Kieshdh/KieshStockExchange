using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KieshStockExchange.Tests;

/// <summary>
/// §A2 herding math: locks the §4c relationship that coordination beats the LLN. With a fraction
/// <c>f</c> of bots tilted by <c>δ</c> in the regime direction, the net buy-fraction imbalance is
/// <c>≈ f·2δ</c> (independent of N) — a follower whose buy-probability is shifted by +δ contributes
/// <c>2δ</c> to buy−sell, and the cohort hash should select ≈f of the fleet.
/// </summary>
public class RegimeImbalanceTests
{
    [Fact]
    public void Follower_cohort_and_imbalance_match_f_and_2delta()
    {
        const int N = 20_000;
        const decimal f = 0.25m;
        const decimal delta = 0.10m;

        var regime = new BotRegimeService(NullLogger<BotRegimeService>.Instance,
            enabled: true, regimeMeanSec: 960.0);
        // Reset opens deterministically at +1 and we do NOT tick, so the regime sign is a known +1.
        regime.Reset(TimeHelper.NowUtc());

        int followers = 0;
        decimal tiltSum = 0m;
        for (int aiUserId = 1; aiUserId <= N; aiUserId++)
        {
            if (regime.IsFollower(aiUserId, f)) followers++;
            tiltSum += regime.HerdTilt(aiUserId, f, delta);
        }

        double followerFraction = (double)followers / N;
        // Each follower's buyProb shifts by +δ, so its buy−sell imbalance is 2δ; fleet imbalance = 2·meanTilt.
        double imbalance = 2.0 * (double)tiltSum / N;

        Assert.InRange(followerFraction, 0.23, 0.27);          // hash selects ≈ f of the fleet
        Assert.InRange(imbalance, 0.045, 0.055);               // ≈ f·2δ = 0.25·0.20 = 0.05
    }

    [Fact]
    public void Disabled_regime_never_selects_followers_and_tilts_zero()
    {
        var regime = new BotRegimeService(NullLogger<BotRegimeService>.Instance, enabled: false);
        regime.Reset(TimeHelper.NowUtc());
        // f = 0 means "no followers" regardless of enabled; verify the cohort + tilt collapse cleanly.
        for (int id = 1; id <= 1000; id++)
        {
            Assert.False(regime.IsFollower(id, 0m));
            Assert.Equal(0m, regime.HerdTilt(id, 0m, 0.10m));
        }
        Assert.Equal(1m, regime.RegimeSign); // deterministic +1 open
    }
}
