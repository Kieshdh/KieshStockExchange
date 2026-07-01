using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §dip-buy (down-drift fix): pins the pure tilt that deploys idle cash on dips. buyProb boost =
/// strength × dip-depth (value-gap &gt; 0) × excess-cash ((cashPrc − MaxReserve)/(1 − MaxReserve)). Inert unless
/// the bot is BOTH below its anchor AND cash-rich — so it adds two-sided (buy) support only where the hoarded
/// cash exists and the price has dropped, never up-side pressure. Default strength 0 ⇒ byte-identical.
/// </summary>
public class DipBuyTiltTests
{
    [Fact]
    public void Inert_when_strength_zero()
        => Assert.Equal(0m, AiBotDecisionService.DipBuyTilt(rawValueGap: 0.10m, cashPrc: 0.80m, maxReserve: 0.30m, strength: 0m));

    [Fact]
    public void Inert_above_anchor_not_a_dip()
        => Assert.Equal(0m, AiBotDecisionService.DipBuyTilt(rawValueGap: -0.10m, cashPrc: 0.80m, maxReserve: 0.30m, strength: 2m));

    [Fact]
    public void Inert_when_cash_at_or_below_reserve()
        => Assert.Equal(0m, AiBotDecisionService.DipBuyTilt(rawValueGap: 0.10m, cashPrc: 0.30m, maxReserve: 0.30m, strength: 2m));

    [Fact]
    public void Inert_when_reserve_is_full()
        => Assert.Equal(0m, AiBotDecisionService.DipBuyTilt(rawValueGap: 0.10m, cashPrc: 0.99m, maxReserve: 1.0m, strength: 2m));

    [Fact]
    public void Fires_scaled_by_dip_depth_and_excess_cash()
    {
        // excess = (0.80 − 0.30)/(1 − 0.30); tilt = strength × dip × excess
        var expected = 2m * 0.10m * ((0.80m - 0.30m) / (1m - 0.30m));
        Assert.Equal(expected, AiBotDecisionService.DipBuyTilt(0.10m, 0.80m, 0.30m, 2m));
    }

    [Fact]
    public void Monotone_in_dip_depth_and_cash()
    {
        var shallowPoorCash = AiBotDecisionService.DipBuyTilt(0.05m, 0.50m, 0.30m, 2m);
        var deepRichCash    = AiBotDecisionService.DipBuyTilt(0.20m, 0.90m, 0.30m, 2m);
        Assert.True(deepRichCash > shallowPoorCash);
        Assert.True(shallowPoorCash > 0m);
    }
}
