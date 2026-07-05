using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §correlation lever: the per-stock / global sentiment-ring σ multipliers. Cross-stock return correlation =
/// shared²/(shared²+idiosyncratic²); lowering per-stock σ + raising global σ tilts that ratio ⇒ higher correlation.
/// Pins the pure effective-σ helpers; mult 1.0 ⇒ byte-identical to the hardcoded arrays (the off path). Mirrors
/// GlobalShockDeltaTests (pure helper, no full-service construction — that does disk I/O + heavy mocks).
/// </summary>
public class SentimentSigmaMultTests
{
    // The hardcoded base σ arrays inside BotSentimentService (PerStockSigma / GlobalSigma).
    static readonly double[] PerStockBase = { 0.25, 0.25, 0.20, 0.12, 0.08 };
    static readonly double[] GlobalBase   = { 0.10, 0.08, 0.06 };

    [Fact]
    public void PerStock_mult_one_is_byte_identical_to_the_base_array()
    {
        Assert.Equal(PerStockBase.Length, BotSentimentService.PerStockRingCount);
        for (int k = 0; k < BotSentimentService.PerStockRingCount; k++)
            Assert.Equal(PerStockBase[k], BotSentimentService.EffectivePerStockSigma(k, 1.0, 1.0), 15);
    }

    [Fact]
    public void Global_mult_one_is_byte_identical_to_the_base_array()
    {
        Assert.Equal(GlobalBase.Length, BotSentimentService.GlobalRingCount);
        for (int k = 0; k < BotSentimentService.GlobalRingCount; k++)
            Assert.Equal(GlobalBase[k], BotSentimentService.EffectiveGlobalSigma(k, 1.0), 15);
    }

    [Fact]
    public void PerStock_mult_half_halves_every_ring()
    {
        for (int k = 0; k < BotSentimentService.PerStockRingCount; k++)
            Assert.Equal(PerStockBase[k] * 0.5, BotSentimentService.EffectivePerStockSigma(k, 1.0, 0.5), 15);
    }

    [Fact]
    public void Global_mult_two_doubles_every_ring()
    {
        for (int k = 0; k < BotSentimentService.GlobalRingCount; k++)
            Assert.Equal(GlobalBase[k] * 2.0, BotSentimentService.EffectiveGlobalSigma(k, 2.0), 15);
    }

    [Fact]
    public void PerStock_mult_composes_multiplicatively_with_any_slow_damp()
    {
        // The mult is a clean final scale on top of the SlowRingDamp fold, so the two levers compose without
        // interaction: EffectivePerStockSigma(k, d, m) == EffectivePerStockSigma(k, d, 1.0) * m for any damp d.
        foreach (double d in new[] { 1.0, 0.5, 0.2 })
            for (int k = 0; k < BotSentimentService.PerStockRingCount; k++)
                Assert.Equal(BotSentimentService.EffectivePerStockSigma(k, d, 1.0) * 0.7,
                             BotSentimentService.EffectivePerStockSigma(k, d, 0.7), 15);
    }
}
