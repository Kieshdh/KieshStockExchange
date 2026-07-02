using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §bear-short (up/down symmetry fix): pins the pure boost that gives the sell side sentiment-driven ammo.
/// The extra advanced-short bucket width = strength × bearishness, where bearishness = the NEGATIVE part of the
/// watchlist sentiment clamped to [0,1]. Inert unless the bot is bearish (sentiment &lt; 0). Default strength 0 ⇒
/// boost 0 ⇒ byte-identical (the caller also skips the sentiment read + the tail bucket is unreachable). Mirrors
/// the cash-funded buy side so a flat bearish bot can act (short) instead of no-op'ing.
/// </summary>
public class BearShortBoostTests
{
    [Fact]
    public void Inert_when_strength_zero()
        => Assert.Equal(0m, AiBotDecisionService.BearShortBoost(strength: 0m, watchlistSentiment: -0.80m));

    [Fact]
    public void Inert_when_bullish_or_neutral()
    {
        Assert.Equal(0m, AiBotDecisionService.BearShortBoost(strength: 2m, watchlistSentiment: 0.50m)); // bullish
        Assert.Equal(0m, AiBotDecisionService.BearShortBoost(strength: 2m, watchlistSentiment: 0m));    // neutral
    }

    [Fact]
    public void Fires_scaled_by_bearishness()
        // boost = strength × (−sentiment) for sentiment in (−1, 0)
        => Assert.Equal(2m * 0.40m, AiBotDecisionService.BearShortBoost(2m, -0.40m));

    [Fact]
    public void Bearishness_clamped_at_one()
        // sentiment past −1 saturates at bearishness 1 ⇒ boost == strength (bucket never exceeds the dial)
        => Assert.Equal(2m, AiBotDecisionService.BearShortBoost(2m, -1.50m));

    [Fact]
    public void Monotone_in_bearishness()
    {
        var mild = AiBotDecisionService.BearShortBoost(2m, -0.20m);
        var deep = AiBotDecisionService.BearShortBoost(2m, -0.70m);
        Assert.True(deep > mild);
        Assert.True(mild > 0m);
    }
}
