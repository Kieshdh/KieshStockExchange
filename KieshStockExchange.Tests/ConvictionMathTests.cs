using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §conviction: the pure decision math for the discretionary sentiment/sector-momentum cohort
/// (<see cref="ConvictionDecisionService"/>). Locks the Hot composition, the hashed-dial ranges + determinism,
/// the taker-entry gate, the memoryless exit, and the CK-safe bet sizing so a refactor can't drift them.
/// </summary>
public class ConvictionMathTests
{
    // Council default weights (kept in sync with AiTradeService / appsettings).
    private const double WSec = 1.0, WMom = 0.5, WGlobal = 0.3, WIdio = 0.2, WOver = 0.5;

    private static double Hot(double sectorSent, double mom, double global, double idio, double gap, int lean)
        => ConvictionDecisionService.Hot(sectorSent, mom, global, idio, gap, lean, WSec, WMom, WGlobal, WIdio, WOver);

    #region Hot composition
    [Fact]
    public void Hot_isolates_each_weighted_term_for_a_chaser()
    {
        Assert.Equal(WSec,    Hot(1, 0, 0, 0, 0, +1), 10);
        Assert.Equal(WMom,    Hot(0, 1, 0, 0, 0, +1), 10);
        Assert.Equal(WGlobal, Hot(0, 0, 1, 0, 0, +1), 10);
        Assert.Equal(WIdio,   Hot(0, 0, 0, 1, 0, +1), 10);
        // gap > 0 (undervalued) is NOT rewarded — the overvaluation veto only bites when gap < 0.
        Assert.Equal(0.0,     Hot(0, 0, 0, 0, 0.5, +1), 10);
    }

    [Fact]
    public void Fader_negates_only_sentiment_and_momentum()
    {
        // Lean −1 flips Wsec/Wmom but leaves global, idio and the veto untouched.
        Assert.Equal(-WSec,    Hot(1, 0, 0, 0, 0, -1), 10);
        Assert.Equal(-WMom,    Hot(0, 1, 0, 0, 0, -1), 10);
        Assert.Equal(WGlobal,  Hot(0, 0, 1, 0, 0, -1), 10);
        Assert.Equal(WIdio,    Hot(0, 0, 0, 1, 0, -1), 10);
    }

    [Fact]
    public void Overvaluation_is_a_one_way_veto()
    {
        // gap < 0 (overvalued) subtracts Wover·|gap|; it can only LOWER Hot, never raise it.
        double overvalued = Hot(0.5, 0, 0, 0, -0.20, +1);
        double fair       = Hot(0.5, 0, 0, 0,  0.00, +1);
        Assert.True(overvalued < fair);
        Assert.Equal(fair - WOver * 0.20, overvalued, 10);

        // A strong positive gap (undervalued) never manufactures conviction on its own.
        Assert.Equal(0.0, Hot(0, 0, 0, 0, 0.99, +1), 10);
    }

    [Fact]
    public void Sentiment_and_momentum_dominate_the_veto()
    {
        // Wsec + Wmom >> Wover: a hot name only mildly above estimate still ranks positive.
        double hot = Hot(0.6, 0.3, 0.1, 0.0, -0.05, +1);
        Assert.True(hot > 0, $"expected sentiment-led positive conviction, got {hot}");
    }
    #endregion

    #region Hashed dials
    [Fact]
    public void Dial_stays_in_range_and_is_deterministic()
    {
        for (int id = 1; id <= 500; id++)
        {
            double d = ConvictionDecisionService.Dial(id, 0x0C01, 0.55, 0.85);
            Assert.InRange(d, 0.55, 0.85);
            Assert.Equal(d, ConvictionDecisionService.Dial(id, 0x0C01, 0.55, 0.85), 12); // same id ⇒ same value
        }
    }

    [Fact]
    public void Dial_disperses_across_bots_and_salts()
    {
        // Different bots get different dials (heterogeneity), and different salts decorrelate a bot's dials.
        Assert.NotEqual(ConvictionDecisionService.Dial(101, 0x0C01, 0.0, 1.0),
                        ConvictionDecisionService.Dial(102, 0x0C01, 0.0, 1.0));
        Assert.NotEqual(ConvictionDecisionService.Dial(101, 0x0C01, 0.0, 1.0),
                        ConvictionDecisionService.Dial(101, 0x0C02, 0.0, 1.0));
    }

    [Fact]
    public void Lean_splits_roughly_chaser_65_fader_35()
    {
        int chasers = 0, faders = 0;
        for (int id = 1; id <= 4000; id++)
            if (ConvictionDecisionService.Lean(id, 0.65) > 0) chasers++; else faders++;
        // Deterministic and stable; chasers should clearly outnumber faders near the 65/35 split.
        Assert.True(chasers > faders, $"chasers ({chasers}) should outnumber faders ({faders})");
        double frac = chasers / 4000.0;
        Assert.InRange(frac, 0.60, 0.70);
    }
    #endregion

    #region Entry / exit gates
    [Fact]
    public void PassesBar_scales_the_bar_by_sensitivity()
    {
        // A more-sensitive bot (higher Sens) clears a lower effective bar.
        Assert.True(ConvictionDecisionService.PassesBar(hot: 0.05, bar: 0.06, sens: 1.5));  // 0.06/1.5 = 0.04 ≤ 0.05
        Assert.False(ConvictionDecisionService.PassesBar(hot: 0.05, bar: 0.06, sens: 0.5)); // 0.06/0.5 = 0.12 > 0.05
        Assert.False(ConvictionDecisionService.PassesBar(hot: 0.10, bar: 0.06, sens: 0.0)); // degenerate ⇒ never acts
    }

    [Fact]
    public void ShouldExit_fires_on_any_of_the_three_conditions()
    {
        const double exitBar = 0.0, stopOver = 0.10;
        // Healthy held name: positive conviction, positive momentum, not overvalued ⇒ hold.
        Assert.False(ConvictionDecisionService.ShouldExit(hot: 0.20, mom: 0.01, overvaluation: 0.02, exitBar, stopOver));
        // Thesis decayed below the exit bar.
        Assert.True(ConvictionDecisionService.ShouldExit(hot: -0.01, mom: 0.01, overvaluation: 0.0, exitBar, stopOver));
        // Momentum flipped negative.
        Assert.True(ConvictionDecisionService.ShouldExit(hot: 0.20, mom: -0.001, overvaluation: 0.0, exitBar, stopOver));
        // Printed overvalued past the stop.
        Assert.True(ConvictionDecisionService.ShouldExit(hot: 0.20, mom: 0.01, overvaluation: 0.15, exitBar, stopOver));
    }

    [Fact]
    public void ShouldExitHeld_holds_through_drawdown_until_horizon_then_exits_on_thesis_decay()
    {
        const double exitBar = 0.0, stopOver = 0.10;
        // Hard exit (overvalued past the stop) ALWAYS fires — bypasses the horizon even when just entered.
        Assert.True(ConvictionDecisionService.ShouldExitHeld(hot: 0.20, overvaluation: 0.15, exitBar, stopOver, heldSec: 10, holdSec: 9999));
        // Thesis decayed (Hot < ExitBar) but the intended hold hasn't elapsed ⇒ HOLD THROUGH the drawdown.
        Assert.False(ConvictionDecisionService.ShouldExitHeld(hot: -0.05, overvaluation: 0.0, exitBar, stopOver, heldSec: 100, holdSec: 1000));
        // Thesis decayed AND the hold elapsed ⇒ exit + rotate.
        Assert.True(ConvictionDecisionService.ShouldExitHeld(hot: -0.05, overvaluation: 0.0, exitBar, stopOver, heldSec: 2000, holdSec: 1000));
        // Healthy conviction past the horizon ⇒ still HOLD (no thesis break, and no momentum knee-jerk here).
        Assert.False(ConvictionDecisionService.ShouldExitHeld(hot: 0.20, overvaluation: 0.0, exitBar, stopOver, heldSec: 9999, holdSec: 1000));
    }

    [Fact]
    public void HoldSec_dial_stays_in_range_and_is_deterministic()
    {
        for (int id = 1; id <= 500; id++)
        {
            double h = ConvictionDecisionService.Dial(id, 0x0C08, 1800.0, 172_800.0);
            Assert.InRange(h, 1800.0, 172_800.0);
            Assert.Equal(h, ConvictionDecisionService.Dial(id, 0x0C08, 1800.0, 172_800.0), 6);
        }
    }
    #endregion

    #region CK-safe sizing
    [Fact]
    public void DeployNotional_risk_appetite_bound_bites_when_cash_is_ample()
    {
        // Ample headroom (avail − floor = 90k > 50k) ⇒ the RiskAppetite notional caps the bet.
        Assert.Equal(50_000m, ConvictionDecisionService.DeployNotional(riskNotional: 50_000m, availCash: 200_000m, cashFloorAmount: 110_000m));
    }

    [Fact]
    public void DeployNotional_respects_floor_and_clamps_at_zero()
    {
        // Headroom (avail − floor = 40k) is smaller than the risk notional (50k) ⇒ deploy only the headroom.
        Assert.Equal(40_000m, ConvictionDecisionService.DeployNotional(50_000m, 150_000m, 110_000m));
        // Below the floor ⇒ deploy nothing (a buy can never breach the cash floor).
        Assert.Equal(0m, ConvictionDecisionService.DeployNotional(50_000m, 100_000m, 110_000m));
        // Never negative.
        Assert.Equal(0m, ConvictionDecisionService.DeployNotional(50_000m, 0m, 110_000m));
        // Result is always ≤ available cash.
        for (int i = 0; i < 50; i++)
        {
            decimal avail = 1000m * i;
            decimal d = ConvictionDecisionService.DeployNotional(50_000m, avail, 110_000m);
            Assert.True(d <= avail);
            Assert.True(d >= 0m);
        }
    }

    [Fact]
    public void ConvictionDeployFraction_is_convex_most_small_rare_large()
    {
        const double scale = 0.12, maxD = 0.90, gamma = 3.0;
        // At/below the bar ⇒ deploy nothing.
        Assert.Equal(0.0, ConvictionDecisionService.ConvictionDeployFraction(0.0, scale, maxD, gamma), 10);
        Assert.Equal(0.0, ConvictionDecisionService.ConvictionDeployFraction(-0.05, scale, maxD, gamma), 10);
        // Exceptional conviction (strength ≥ scale ⇒ z=1) ⇒ near-full MaxDeploy.
        Assert.Equal(maxD, ConvictionDecisionService.ConvictionDeployFraction(scale, scale, maxD, gamma), 10);
        Assert.Equal(maxD, ConvictionDecisionService.ConvictionDeployFraction(scale * 5, scale, maxD, gamma), 10);
        // Convex: HALF-strength deploys only maxD·0.5³ = maxD·0.125 (most plays SMALL).
        Assert.Equal(maxD * 0.125, ConvictionDecisionService.ConvictionDeployFraction(scale * 0.5, scale, maxD, gamma), 10);
        // Monotonic non-decreasing, bounded [0, maxD].
        double prev = -1;
        for (int i = 0; i <= 20; i++)
        {
            double f = ConvictionDecisionService.ConvictionDeployFraction(scale * i / 10.0, scale, maxD, gamma);
            Assert.InRange(f, 0.0, maxD);
            Assert.True(f >= prev - 1e-12);
            prev = f;
        }
    }
    #endregion

    #region P3 shorting route
    [Fact]
    public void ShouldOpenShort_needs_overvaluation_past_the_bar_and_non_rising_momentum()
    {
        const double bar = 0.06;
        // Strongly overvalued + momentum not rising ⇒ short.
        Assert.True(ConvictionDecisionService.ShouldOpenShort(overvaluation: 0.10, mom: 0.0, bar));
        Assert.True(ConvictionDecisionService.ShouldOpenShort(overvaluation: 0.06, mom: -0.02, bar)); // at the bar counts
        // Not overvalued enough ⇒ no short (don't short a fairly-priced name).
        Assert.False(ConvictionDecisionService.ShouldOpenShort(overvaluation: 0.03, mom: -0.02, bar));
        // Overvalued but momentum RISING ⇒ don't fight the tape.
        Assert.False(ConvictionDecisionService.ShouldOpenShort(overvaluation: 0.10, mom: 0.01, bar));
    }

    [Fact]
    public void ShouldCoverShort_uses_hysteresis_below_half_the_bar_or_rising_momentum()
    {
        const double bar = 0.06; // open at >=0.06, cover at <=0.03 (0.5*bar)
        // Reverted toward fair value (below half the bar) ⇒ cover.
        Assert.True(ConvictionDecisionService.ShouldCoverShort(overvaluation: 0.02, mom: 0.0, bar));
        Assert.True(ConvictionDecisionService.ShouldCoverShort(overvaluation: 0.03, mom: 0.0, bar)); // at the band counts
        // Momentum turned up against the short ⇒ cover even if still overvalued.
        Assert.True(ConvictionDecisionService.ShouldCoverShort(overvaluation: 0.10, mom: 0.005, bar));
        // Still overvalued in the hysteresis dead-band, momentum not rising ⇒ HOLD the short (no thrash).
        Assert.False(ConvictionDecisionService.ShouldCoverShort(overvaluation: 0.05, mom: 0.0, bar));

        // The dead-band exists: an open-worthy overvaluation (>=bar) that hasn't reverted is NOT a cover, so a
        // just-opened short is not immediately covered (open and cover cannot both fire on the same reading).
        Assert.True(ConvictionDecisionService.ShouldOpenShort(0.07, -0.01, bar));
        Assert.False(ConvictionDecisionService.ShouldCoverShort(0.07, -0.01, bar));
    }

    [Fact]
    public void ShortQty_is_a_small_floored_exposure_and_zero_on_bad_inputs()
    {
        // 0.20 risk * 0.15 fraction * 200k seed = 6000 notional; /100 price = 60 shares.
        Assert.Equal(60, ConvictionDecisionService.ShortQty(seedNotional: 200_000m, riskAppetite: 0.20, shortRiskFraction: 0.15, price: 100.0));
        // Smaller than a whole share ⇒ 0 (never a fractional/oversized short).
        Assert.Equal(0, ConvictionDecisionService.ShortQty(200_000m, 0.20, 0.15, price: 1_000_000.0));
        // Degenerate inputs ⇒ 0.
        Assert.Equal(0, ConvictionDecisionService.ShortQty(200_000m, 0.0, 0.15, 100.0));
        Assert.Equal(0, ConvictionDecisionService.ShortQty(200_000m, 0.20, 0.0, 100.0));
        Assert.Equal(0, ConvictionDecisionService.ShortQty(200_000m, 0.20, 0.15, price: 0.0));
        // Exposure scales with the fraction (a bigger ShortRiskFraction ⇒ a bigger short).
        Assert.True(ConvictionDecisionService.ShortQty(200_000m, 0.20, 0.30, 100.0)
                  > ConvictionDecisionService.ShortQty(200_000m, 0.20, 0.15, 100.0));
    }
    #endregion
}
