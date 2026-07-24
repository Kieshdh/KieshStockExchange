using System;
using System.Linq;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §trend-follower (realism overhaul step 3): pins the pure chartist tilt + cohort membership. A cohort member
/// CHASES recent price momentum (momentumSignal ±5%→±1); a contrarian member FADES it; a per-bot strength stagger
/// scales magnitude. Enabled=false / Strength 0 skips the block entirely (byte-identical) — not exercised here.
/// </summary>
public class TrendFollowTiltTests
{
    const decimal Strength = 0.30m, Stagger = 1.0m;

    [Fact]
    public void Chases_momentum_up()
        => Assert.True(AiBotDecisionService.TrendFollowTilt(0.5m, Strength, Stagger, contrarian: false) > 0m);

    [Fact]
    public void Chases_momentum_down()
        => Assert.True(AiBotDecisionService.TrendFollowTilt(-0.5m, Strength, Stagger, contrarian: false) < 0m);

    [Fact]
    public void Contrarian_fades_the_move()
    {
        var chase = AiBotDecisionService.TrendFollowTilt(0.5m, Strength, Stagger, contrarian: false);
        var fade = AiBotDecisionService.TrendFollowTilt(0.5m, Strength, Stagger, contrarian: true);
        Assert.Equal(-chase, fade);   // the contrarian is the exact opposite of the chaser
    }

    [Fact]
    public void Stagger_scales_magnitude()
    {
        var soft = AiBotDecisionService.TrendFollowTilt(0.5m, Strength, 0.5m, contrarian: false);
        var hard = AiBotDecisionService.TrendFollowTilt(0.5m, Strength, 1.5m, contrarian: false);
        Assert.True(hard > soft && hard == soft * 3m);
    }

    [Fact]
    public void Cohort_fraction_edges()
    {
        Assert.False(AiBotDecisionService.IsTrendFollower(12345, 0.0, 0x7A3D));   // 0 ⇒ never
        Assert.True(AiBotDecisionService.IsTrendFollower(12345, 1.0, 0x7A3D));    // 1 ⇒ always
    }

    [Fact]
    public void Cohort_membership_matches_fraction_and_is_deterministic()
    {
        const int salt = 0x7A3D;
        var members = Enumerable.Range(20000, 4000).Count(id => AiBotDecisionService.IsTrendFollower(id, 0.25, salt));
        Assert.InRange(members / 4000.0, 0.20, 0.30);   // ~25% selected
        Assert.Equal(AiBotDecisionService.IsTrendFollower(20001, 0.25, salt),
                     AiBotDecisionService.IsTrendFollower(20001, 0.25, salt));   // deterministic
    }

    // §taker-coupling (step 3 v2): a strong momentum makes a member CROSS THE SPREAD (taker) in the momentum direction.
    const decimal Thr = 0.05m;

    [Fact]
    public void Taker_below_threshold_no_override()
        => Assert.False(AiBotDecisionService.TrendTakerDecision(0.03m, 1.0m, contrarian: false, draw: 0m, threshold: Thr).over);

    [Fact]
    public void Taker_strong_up_crosses_spread_as_market_buy()
    {
        var (over, isMarket, isBuy) = AiBotDecisionService.TrendTakerDecision(1.0m, 1.0m, contrarian: false, draw: 0m, threshold: Thr);
        Assert.True(over && isMarket && isBuy);   // p = clamp(1·1)=1 ⇒ fires; market buy
    }

    [Fact]
    public void Taker_strong_down_crosses_spread_as_market_sell()
    {
        var (over, isMarket, isBuy) = AiBotDecisionService.TrendTakerDecision(-1.0m, 1.0m, contrarian: false, draw: 0m, threshold: Thr);
        Assert.True(over && isMarket && !isBuy);   // market sell (chase the down-move)
    }

    [Fact]
    public void Taker_contrarian_fades_the_move()
    {
        var (over, _, isBuy) = AiBotDecisionService.TrendTakerDecision(1.0m, 1.0m, contrarian: true, draw: 0m, threshold: Thr);
        Assert.True(over && !isBuy);   // +momentum ⇒ contrarian SELLS
    }

    [Fact]
    public void Taker_probability_scales_with_strength_times_momentum()
    {
        // strength 1.0, momentum 0.5 ⇒ p = 0.5: a draw below fires, a draw above does not.
        Assert.True(AiBotDecisionService.TrendTakerDecision(0.5m, 1.0m, false, draw: 0.4m, threshold: Thr).over);
        Assert.False(AiBotDecisionService.TrendTakerDecision(0.5m, 1.0m, false, draw: 0.6m, threshold: Thr).over);
    }

    // §shared-factor chase reuses TrendTakerDecision with the SHARED global signal (~±0.2-0.4). Because that signal
    // is smaller than per-stock momentum (±1), the SharedChaseWeight must be larger to reach a comparable probability.
    [Fact]
    public void SharedChase_global_signal_up_forces_market_buy()
    {
        // global +0.3, weight 2.0 ⇒ p = 0.6: fires with a low draw, in the up (buy) direction.
        var (over, isMarket, isBuy) = AiBotDecisionService.TrendTakerDecision(0.3m, 2.0m, contrarian: false, draw: 0.4m, threshold: Thr);
        Assert.True(over);
        Assert.True(isMarket);
        Assert.True(isBuy);
    }

    [Fact]
    public void SharedChase_global_signal_down_forces_market_sell()
    {
        var (over, isMarket, isBuy) = AiBotDecisionService.TrendTakerDecision(-0.3m, 2.0m, contrarian: false, draw: 0.4m, threshold: Thr);
        Assert.True(over);
        Assert.True(isMarket);
        Assert.False(isBuy);
    }

    [Fact]
    public void SharedChase_probability_scales_with_weight_times_global()
    {
        // weight 2.0, |global| 0.3 ⇒ p = 0.6: a draw below fires, a draw above does not.
        Assert.True(AiBotDecisionService.TrendTakerDecision(0.3m, 2.0m, false, draw: 0.5m, threshold: Thr).over);
        Assert.False(AiBotDecisionService.TrendTakerDecision(0.3m, 2.0m, false, draw: 0.7m, threshold: Thr).over);
    }

    // ---- §μ-engine B per-stock effective bias = mean ± spread·hash(stock) --------------------------

    [Fact]
    public void EffectiveBias_spreadOff_returns_mean_exactly()
    {
        // spread ≤ 0 ⇒ mean unchanged for every stock (byte-identical to the mean-only μ-engine B).
        for (int sid = 1; sid <= 50; sid++)
        {
            Assert.Equal(0.03m, AiBotDecisionService.EffectiveRegimeBias(sid, 0.03m, 0m));
            Assert.Equal(0.03m, AiBotDecisionService.EffectiveRegimeBias(sid, 0.03m, -0.1m));
        }
    }

    [Fact]
    public void EffectiveBias_deterministic_and_within_band()
    {
        const decimal mean = 0.03m, spread = 0.06m;
        for (int sid = 1; sid <= 200; sid++)
        {
            var a = AiBotDecisionService.EffectiveRegimeBias(sid, mean, spread);
            Assert.Equal(a, AiBotDecisionService.EffectiveRegimeBias(sid, mean, spread)); // deterministic
            Assert.InRange(a, mean - spread, mean + spread);                              // bounded
        }
    }

    [Fact]
    public void EffectiveBias_crossSection_mean_is_the_mean_dispersion_from_spread()
    {
        // Aggregate drift = the MEAN (E[z]=0); dispersion comes from the SPREAD ⇒ stocks vary.
        const decimal mean = 0.03m, spread = 0.06m;
        var vals = Enumerable.Range(1, 3000)
            .Select(sid => AiBotDecisionService.EffectiveRegimeBias(sid, mean, spread)).ToList();
        var avg = vals.Average();
        Assert.True(Math.Abs(avg - mean) < 0.004m, $"cross-section mean {avg} should ≈ {mean}");
        // Spread ≳ mean ⇒ a meaningful fraction of stocks are net-NEGATIVE (down-trenders), not a lockstep climb.
        var netDown = vals.Count(v => v < 0m);
        Assert.InRange(netDown, 1, vals.Count - 1);
        // and some are clearly above the mean (up-trenders) ⇒ genuine dispersion.
        Assert.Contains(vals, v => v > mean + spread * 0.5m);
    }

    [Fact]
    public void EffectiveBias_zeroMean_positiveSpread_is_zeroMean_dispersion()
    {
        // mean 0 + spread>0 ⇒ pure per-stock variety, aggregate ≈ 0 (stocks vary, net-flat market).
        var vals = Enumerable.Range(1, 3000)
            .Select(sid => AiBotDecisionService.EffectiveRegimeBias(sid, 0m, 0.06m)).ToList();
        Assert.True(Math.Abs(vals.Average()) < 0.004m);
        Assert.Contains(vals, v => v > 0.02m);
        Assert.Contains(vals, v => v < -0.02m);
    }
}
