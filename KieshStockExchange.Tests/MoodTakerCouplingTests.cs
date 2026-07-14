using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §per-strategy F&amp;G reaction: the pure taker-share math for <see cref="AiBotDecisionService.MoodTakerMult"/>
/// (Feature 1 per-strategy multiplier) and <see cref="AiBotDecisionService.JointTakerShare"/> (the joint-cap
/// guardrail). Locks the per-strategy sign/gain directions, the exempt (gain-0) degeneracy, and the ≤ cap×base
/// bound so a refactor can't silently amplify the taker channel.
/// </summary>
public class MoodTakerCouplingTests
{
    // The council per-strategy table (kept in sync with AiTradeService defaults / appsettings).
    private static readonly (AiStrategy Strat, double GGreed, double GFear, int Sign)[] Table =
    {
        (AiStrategy.TrendFollower, 0.12, 0.10, +1),
        (AiStrategy.MeanReversion, 0.08, 0.08, +1),
        (AiStrategy.Scalper,       0.05, 0.05, +1),
        (AiStrategy.Conviction,    0.05, 0.00, -1),
        (AiStrategy.Random,        0.10, 0.07, +1),
    };
    private const double Cap = 0.15;

    private static double Mult(AiStrategy s, double tilt)
    {
        var row = System.Array.Find(Table, r => r.Strat == s);
        return AiBotDecisionService.MoodTakerMult(tilt, row.GGreed, row.GFear, row.Sign, Cap);
    }

    #region Neutral fixed point + bounds
    [Fact]
    public void Neutral_mood_is_a_unit_multiplier_for_every_strategy()
    {
        foreach (var row in Table)
            Assert.Equal(1.0, AiBotDecisionService.MoodTakerMult(0.0, row.GGreed, row.GFear, row.Sign, Cap), 12);
    }

    [Fact]
    public void Multiplier_never_leaves_the_one_plusminus_cap_band()
    {
        foreach (var tilt in new[] { -1.0, -0.5, -0.1, 0.0, 0.1, 0.5, 1.0 })
            foreach (var row in Table)
            {
                double m = AiBotDecisionService.MoodTakerMult(tilt, row.GGreed, row.GFear, row.Sign, Cap);
                Assert.InRange(m, 1.0 - Cap, 1.0 + Cap);
            }
    }
    #endregion

    #region Per-strategy sign + direction
    [Fact]
    public void Chasers_raise_taker_share_in_both_greed_and_fear()
    {
        // Sign +1 pro-cyclical: greed (tilt>0) and fear (tilt<0) both push the multiplier above 1.
        Assert.True(Mult(AiStrategy.TrendFollower, +0.5) > 1.0);
        Assert.True(Mult(AiStrategy.TrendFollower, -0.5) > 1.0);
        Assert.True(Mult(AiStrategy.Random, +0.5) > 1.0);
        Assert.True(Mult(AiStrategy.Random, -0.5) > 1.0);
    }

    [Fact]
    public void Momentum_reacts_harder_than_scalper_at_the_same_tilt()
    {
        Assert.True(Mult(AiStrategy.TrendFollower, +0.5) > Mult(AiStrategy.Scalper, +0.5));
        Assert.True(Mult(AiStrategy.TrendFollower, -0.5) > Mult(AiStrategy.Scalper, -0.5));
    }

    [Fact]
    public void Conviction_fades_greed_and_is_flat_in_fear()
    {
        // Sign −1 + GainGreed 0.05 ⇒ greed TRIMS aggression (multiplier < 1).
        Assert.True(Mult(AiStrategy.Conviction, +0.5) < 1.0);
        // GainFear 0.0 ⇒ the fear side is handled by Feature 3, so no taker change here.
        Assert.Equal(1.0, Mult(AiStrategy.Conviction, -0.9), 12);
    }

    [Fact]
    public void Exempt_or_untabled_strategy_gets_no_coupling()
    {
        // A strategy absent from the table is looked up as gain 0 / sign +1 at the call site ⇒ multiplier is 1.
        Assert.Equal(1.0, AiBotDecisionService.MoodTakerMult(+0.9, 0.0, 0.0, 1, Cap), 12);
        Assert.Equal(1.0, AiBotDecisionService.MoodTakerMult(-0.9, 0.0, 0.0, 1, Cap), 12);
    }
    #endregion

    #region Joint taker-share cap (guardrail)
    [Fact]
    public void Joint_cap_bounds_the_activity_times_mood_product()
    {
        // Two independently-safe multipliers stack: activity 1.4 × mood 1.15 = 1.61× > the 1.5× ceiling ⇒ clamped.
        double baseShare = 0.5;
        double share = AiBotDecisionService.JointTakerShare(baseShare, moodMult: 1.15, activityMult: 1.4, jointCapMult: 1.5);
        Assert.Equal(baseShare * 1.5, share, 12);
    }

    [Fact]
    public void Joint_cap_is_inert_when_the_product_is_within_the_ceiling()
    {
        double baseShare = 0.4;
        double share = AiBotDecisionService.JointTakerShare(baseShare, moodMult: 1.1, activityMult: 1.0, jointCapMult: 1.5);
        Assert.Equal(baseShare * 1.1, share, 12);   // 1.1× < 1.5× ⇒ unchanged
    }

    [Fact]
    public void Joint_cap_output_is_clamped_into_zero_one()
    {
        // A high base with a big product is bounded by BOTH the joint cap and the [0,1] clamp.
        double share = AiBotDecisionService.JointTakerShare(0.9, moodMult: 1.15, activityMult: 2.0, jointCapMult: 1.5);
        Assert.InRange(share, 0.0, 1.0);
        Assert.Equal(1.0, share, 12);   // 0.9×1.5 = 1.35 → clamped to 1
    }
    #endregion
}
