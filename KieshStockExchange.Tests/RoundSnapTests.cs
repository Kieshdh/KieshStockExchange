using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// Order-wall declumping: SnapToRoundNumber with spread=0 snaps to the exact psychologically-significant
/// level (today's wall-building behavior); spread>0 disperses snapped orders within ±spread·unit of that
/// level so volume forms a soft cluster across nearby ticks instead of one monolithic wall.
/// </summary>
public class RoundSnapTests
{
    [Theory]
    [InlineData(148.7, 149.0)]   // >=100 ⇒ unit 1
    [InlineData(150.4, 150.0)]
    [InlineData(23.3, 23.5)]     // >=20  ⇒ unit 0.5
    [InlineData(7.46, 7.5)]      // <20   ⇒ unit 0.1
    [InlineData(512.0, 510.0)]   // >=500 ⇒ unit 5
    public void Exact_snap_when_spread_zero(decimal price, decimal expected)
    {
        Assert.Equal(expected, AiBotDecisionService.SnapToRoundNumber(price));
    }

    [Fact]
    public void Soft_snap_centers_on_the_level_at_jitter_midpoint()
    {
        // jitter01 = 0.5 ⇒ (0.5*2-1)=0 ⇒ no dispersion ⇒ lands exactly on the level even with spread>0.
        Assert.Equal(149.0m, AiBotDecisionService.SnapToRoundNumber(148.7m, spread: 0.3m, jitter01: 0.5m));
    }

    [Fact]
    public void Soft_snap_disperses_within_plus_minus_spread_times_unit()
    {
        // $100+ stock ⇒ unit = 1. spread 0.3 ⇒ dispersion in [-0.3, +0.3] around 149.
        var lo = AiBotDecisionService.SnapToRoundNumber(148.7m, spread: 0.3m, jitter01: 0m);  // -0.3
        var hi = AiBotDecisionService.SnapToRoundNumber(148.7m, spread: 0.3m, jitter01: 1m);  // +0.3
        Assert.Equal(148.7m, lo);
        Assert.Equal(149.3m, hi);
        Assert.True(hi - lo == 0.6m, "full dispersion span should be 2*spread*unit");
    }

    [Fact]
    public void Soft_snap_breaks_a_single_level_into_multiple_distinct_prices()
    {
        // Many orders that would all snap to 149 now spread across distinct prices ⇒ no single wall.
        var prices = new HashSet<decimal>();
        for (int i = 0; i <= 10; i++)
            prices.Add(AiBotDecisionService.SnapToRoundNumber(148.6m, spread: 0.3m, jitter01: i / 10m));
        Assert.True(prices.Count > 5, $"soft snap should yield many levels, got {prices.Count}");
    }

    [Fact]
    public void Never_returns_below_one_cent()
    {
        Assert.True(AiBotDecisionService.SnapToRoundNumber(0.02m, spread: 5m, jitter01: 0m) >= 0.01m);
    }
}
