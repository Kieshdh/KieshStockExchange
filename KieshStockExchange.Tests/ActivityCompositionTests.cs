using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §composition seam — activity → order composition (taker share + limit distance). Pure-core coverage:
/// ComposeClamped (the median-1 clamped factor) and ComposeTakerOverrideKind (the post-pick override),
/// plus the disabled-service ⇒ neutral-1 contract that carries the byte-identical-off guarantee.
/// </summary>
public class ActivityCompositionTests
{
    #region ComposeClamped

    [Fact]
    public void ComposeClamped_NeutralInputs_IsExactlyOne()
    {
        Assert.Equal(1.0, BotActivityService.ComposeClamped(gNorm: 1.0, gExp: 0.5, s: 1.0, floor: 0.4, cap: 3.0));
        Assert.Equal(1.0, BotActivityService.ComposeClamped(gNorm: 1.0, gExp: 1.0, s: 1.0, floor: 0.4, cap: 3.0));
    }

    [Fact]
    public void ComposeClamped_ClampsBothSides()
    {
        Assert.Equal(3.0, BotActivityService.ComposeClamped(2.0, 1.0, 5.0, 0.4, 3.0));
        Assert.Equal(0.4, BotActivityService.ComposeClamped(0.5, 1.0, 0.3, 0.4, 3.0));
    }

    [Fact]
    public void ComposeClamped_GExpDampensGlobalFactor()
    {
        // gNorm 4 at exponent 0.5 contributes ×2, not ×4.
        Assert.Equal(2.0, BotActivityService.ComposeClamped(4.0, 0.5, 1.0, 0.0, 100.0), precision: 12);
        // exponent 0 removes the global factor entirely.
        Assert.Equal(1.5, BotActivityService.ComposeClamped(4.0, 0.0, 1.5, 0.0, 100.0), precision: 12);
    }

    [Fact]
    public void CompositionActivity_DisabledService_ReturnsOne()
    {
        var stocks = new Mock<IStockService>();
        stocks.Setup(s => s.ById).Returns(new Dictionary<int, Stock>());
        var profiles  = new StockProfileService(enabled: false);
        var sentiment = new BotSentimentService(stocks.Object, profiles, NullLogger<BotSentimentService>.Instance);
        var svc = new BotActivityService(stocks.Object, sentiment, NullLogger<BotActivityService>.Instance,
            recentReturn: _ => 0.0, enabled: false);
        Assert.Equal(1.0, svc.CompositionActivity(10));
        svc.Reset(DateTime.UtcNow);
        svc.Tick(DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(1.0, svc.CompositionActivity(10));
    }

    [Fact]
    public void CompositionActivity_EnabledUnknownStockAtReset_IsNeutral()
    {
        var stocks = new Mock<IStockService>();
        stocks.Setup(s => s.ById).Returns(new Dictionary<int, Stock>());
        var profiles  = new StockProfileService(enabled: false);
        var sentiment = new BotSentimentService(stocks.Object, profiles, NullLogger<BotSentimentService>.Instance);
        var svc = new BotActivityService(stocks.Object, sentiment, NullLogger<BotActivityService>.Instance,
            recentReturn: _ => 0.0, enabled: true);
        svc.Reset(DateTime.UtcNow);
        // gNorm resets to 1 and an unknown stock falls back to S=1 ⇒ the composition factor opens neutral.
        Assert.Equal(1.0, svc.CompositionActivity(10));
    }

    #endregion

    #region ComposeTakerOverrideKind

    [Theory]
    [InlineData(0.0)]   // k = 0 ⇒ seam off
    [InlineData(0.5)]
    public void Override_NeutralActivity_NeverConverts(double k)
    {
        Assert.Equal(0, AiBotDecisionService.ComposeTakerOverrideKind(isTaker: false, act: 1.0, k, draw: 0.0m));
        Assert.Equal(0, AiBotDecisionService.ComposeTakerOverrideKind(isTaker: true,  act: 1.0, k, draw: 0.0m));
    }

    [Fact]
    public void Override_Hot_UpgradesLimitAtHazardBoundary()
    {
        // act 3, k 0.5 ⇒ upgrade prob = 1 − 3^−0.5 ≈ 0.42265.
        var p = 1.0 - Math.Pow(3.0, -0.5);
        Assert.Equal(1, AiBotDecisionService.ComposeTakerOverrideKind(false, 3.0, 0.5, (decimal)(p - 0.001)));
        Assert.Equal(0, AiBotDecisionService.ComposeTakerOverrideKind(false, 3.0, 0.5, (decimal)(p + 0.001)));
    }

    [Fact]
    public void Override_Quiet_DowngradesTakerAtHazardBoundary()
    {
        // act 0.4, k 0.5 ⇒ downgrade prob = 1 − 0.4^0.5 ≈ 0.36754 (exact multiplicative m·act^k cut).
        var p = 1.0 - Math.Pow(0.4, 0.5);
        Assert.Equal(-1, AiBotDecisionService.ComposeTakerOverrideKind(true, 0.4, 0.5, (decimal)(p - 0.001)));
        Assert.Equal(0,  AiBotDecisionService.ComposeTakerOverrideKind(true, 0.4, 0.5, (decimal)(p + 0.001)));
    }

    [Fact]
    public void Override_WrongSideOfOne_NeverConverts()
    {
        // Hot never downgrades an existing taker; quiet never upgrades a limit.
        Assert.Equal(0, AiBotDecisionService.ComposeTakerOverrideKind(isTaker: true,  act: 3.0, 0.5, draw: 0.0m));
        Assert.Equal(0, AiBotDecisionService.ComposeTakerOverrideKind(isTaker: false, act: 0.4, 0.5, draw: 0.0m));
    }

    [Fact]
    public void Override_MonotoneInActivity()
    {
        // Fixed draw: a hotter name converts where a milder one doesn't.
        const decimal draw = 0.25m;
        Assert.Equal(0, AiBotDecisionService.ComposeTakerOverrideKind(false, 1.5, 0.5, draw)); // p≈0.18
        Assert.Equal(1, AiBotDecisionService.ComposeTakerOverrideKind(false, 3.0, 0.5, draw)); // p≈0.42
    }

    #endregion

    #region OpenTakerRampMult

    [Fact]
    public void Ramp_OffOrComplete_IsOne()
    {
        Assert.Equal(1.0, AiBotDecisionService.OpenTakerRampMult(1, uptimeMin: 0.0, rampMin: 0.0, staggerMin: 0.0));
        Assert.Equal(1.0, AiBotDecisionService.OpenTakerRampMult(1, uptimeMin: 10.0, rampMin: 10.0, staggerMin: 0.0));
        Assert.Equal(1.0, AiBotDecisionService.OpenTakerRampMult(1, uptimeMin: 99.0, rampMin: 10.0, staggerMin: 8.0));
    }

    [Fact]
    public void Ramp_StartsAtZero_AndIsMonotone()
    {
        Assert.Equal(0.0, AiBotDecisionService.OpenTakerRampMult(1, 0.0, 10.0, 0.0));
        var a = AiBotDecisionService.OpenTakerRampMult(1, 2.0, 10.0, 0.0);
        var b = AiBotDecisionService.OpenTakerRampMult(1, 7.0, 10.0, 0.0);
        Assert.Equal(0.2, a, precision: 12);
        Assert.True(b > a);
    }

    [Fact]
    public void Ramp_Stagger_DesynchronizesStocks()
    {
        // With a stagger window, different stocks sit at different ramp phases at the same uptime.
        var vals = new HashSet<double>();
        for (int sid = 1; sid <= 20; sid++)
            vals.Add(AiBotDecisionService.OpenTakerRampMult(sid, 5.0, 10.0, 8.0));
        Assert.True(vals.Count > 5);                       // genuinely dispersed onsets
        Assert.All(vals, v => Assert.InRange(v, 0.0, 1.0));
        // Deterministic: same stock, same inputs, same value.
        Assert.Equal(AiBotDecisionService.OpenTakerRampMult(7, 5.0, 10.0, 8.0),
                     AiBotDecisionService.OpenTakerRampMult(7, 5.0, 10.0, 8.0));
    }

    #endregion
}
