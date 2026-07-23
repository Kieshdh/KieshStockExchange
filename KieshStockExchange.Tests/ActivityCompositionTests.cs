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

    #region CompositionSizeMult

    [Fact]
    public void Size_Off_Or_Neutral_IsOne()
    {
        Assert.Equal(1.0, AiBotDecisionService.CompositionSizeMult(act: 3.0, k: 0.0, cap: 3.0)); // k=0 off
        Assert.Equal(1.0, AiBotDecisionService.CompositionSizeMult(act: 1.0, k: 1.0, cap: 3.0)); // act=1 median
    }

    [Fact]
    public void Size_HotBigger_QuietSmaller_Monotone()
    {
        var hot   = AiBotDecisionService.CompositionSizeMult(2.0, 1.0, 3.0);
        var quiet = AiBotDecisionService.CompositionSizeMult(0.5, 1.0, 3.0);
        Assert.True(hot > 1.0 && quiet < 1.0);
        Assert.Equal(2.0, hot, precision: 12);
        Assert.Equal(0.5, quiet, precision: 12);
    }

    [Fact]
    public void Size_ClampsBothWays()
    {
        Assert.Equal(3.0, AiBotDecisionService.CompositionSizeMult(10.0, 1.0, 3.0));       // hot cap
        Assert.Equal(1.0 / 3.0, AiBotDecisionService.CompositionSizeMult(0.01, 1.0, 3.0)); // quiet floor
    }

    #endregion

    #region HotSizeMult (§F2 hot-stock rotation — organic multi-timescale)

    // Arbitrary tick timescales for the pure-core tests; only the ratios matter.
    private const long IntraA = 1_000_000L;
    private const long IntraB = 2_700_000L;   // aperiodic 2.7× harmonic
    private const long Daily  = 24_000_000L;

    private static double Hot(int sid, string cls, long now, double dailyW, double boost,
        double tilt = 0.0, double theta = 0.02, double slope = 0.0)
        => BotActivityService.HotSizeMult(sid, cls, now, IntraA, IntraB, Daily, dailyW, boost, tilt, theta, slope);

    [Fact]
    public void Hot_OffWhenBoostAtOrBelowOne_IsExactlyOne()
    {
        Assert.Equal(1.0, Hot(7, "Normal", 12345, 0.4, boost: 1.0));
        Assert.Equal(1.0, BotActivityService.HotSizeMult(7, "Normal", 12345, IntraA, IntraB,
            dailyTicks: 0, dailyWeight: 0.4, boost: 1.5, sentTilt: 0.0, sentTheta: 0.02, slopeAbs: 0.0));
    }

    [Fact]
    public void Hot_AlwaysWithinReciprocalBoostBand()
    {
        const double boost = 1.5;
        for (int sid = 1; sid <= 300; sid++)
            Assert.InRange(Hot(sid, "Meme", sid * 7919L, 0.4, boost, tilt: 0.5, slope: 5.0), 1.0 / boost, boost);
    }

    [Fact]
    public void Hot_IsMedianOne_Redistributive()
    {
        // Geometric mean across the cross-section ≈ 1 (mean log-hotness ≈ 0) ⇒ aggregate volume preserved.
        const double boost = 1.5;
        double logSum = 0; int n = 0;
        long now = 42 * IntraA + IntraA / 3;
        for (int sid = 1; sid <= 2000; sid++) { logSum += Math.Log(Hot(sid, "Normal", now, 0.4, boost)); n++; }
        Assert.Equal(0.0, logSum / n, precision: 1); // mean ln(H) ≈ 0 within 0.05
    }

    [Fact]
    public void Hot_RotatesLeadersOverTime()
    {
        // The hottest names now are largely not the hottest a while later — leadership drifts, not static.
        const double boost = 1.5;
        static HashSet<int> Top(long now) => Enumerable.Range(1, 200)
            .OrderByDescending(sid => Hot(sid, "Normal", now, 0.4, boost)).Take(20).ToHashSet();
        var a = Top(3 * IntraA);
        var b = Top(3 * IntraA + IntraA * 3 / 5);  // ~0.6 of the fast intraday period later
        var overlap = a.Intersect(b).Count() / 20.0;
        Assert.True(overlap < 0.55, $"leader overlap {overlap:P0} should be loose as time advances");
    }

    [Fact]
    public void Hot_Deterministic_SameInputsSameOutput()
        => Assert.Equal(Hot(13, "Volatile", 987654321L, 0.4, 1.5, 0.3, 0.02, 1.1),
                        Hot(13, "Volatile", 987654321L, 0.4, 1.5, 0.3, 0.02, 1.1));

    [Fact]
    public void Hot_PerClassAmplitude_CalmTighterThanMeme()
    {
        // Averaged over the cross-section, a class with a bigger amplitude rotates through a wider band.
        const double boost = 1.5;
        long now = 5 * IntraA + IntraA / 4;
        double MeanAbsLog(string cls) => Enumerable.Range(1, 500)
            .Average(sid => Math.Abs(Math.Log(Hot(sid, cls, now, 0.4, boost))));
        Assert.True(MeanAbsLog("Calm") < MeanAbsLog("Normal"));
        Assert.True(MeanAbsLog("Normal") < MeanAbsLog("Volatile"));
        Assert.True(MeanAbsLog("Volatile") < MeanAbsLog("Meme"));
    }

    [Fact]
    public void Hot_IsSmooth_NoStaticStep()
    {
        // Small time steps ⇒ small hotness changes everywhere (organic drift, no window-edge jump).
        const double boost = 1.5;
        long dt = IntraA / 500;  // fine step
        double maxJump = 0;
        for (long k = 0; k < 400; k++)
        {
            var h0 = Hot(21, "Normal", 7 * IntraA + k * dt,       0.4, boost);
            var h1 = Hot(21, "Normal", 7 * IntraA + (k + 1) * dt, 0.4, boost);
            maxJump = Math.Max(maxJump, Math.Abs(h1 - h0));
        }
        Assert.True(maxJump < 0.02, $"max step {maxJump:F4} should be tiny (smooth drift, no static edge)");
    }

    [Fact]
    public void Hot_DailyLayer_Contributes()
    {
        // The daily component genuinely moves the result: pure-intraday vs pure-daily differ at a generic point.
        const double boost = 1.5;
        long now = 11 * IntraA + IntraA / 7;
        Assert.NotEqual(Hot(19, "Normal", now, 0.0, boost), Hot(19, "Normal", now, 1.0, boost));
    }

    [Fact]
    public void Hot_SentimentTilt_ZeroMeanAndBounded()
    {
        // Above the θ baseline nudges hotness up, below it nudges down, vs the no-tilt value; stays in band.
        const double boost = 1.5, tilt = 0.3, theta = 0.02;
        long now = 9 * IntraA + IntraA / 5;
        var noTilt   = Hot(31, "Normal", now, 0.4, boost, tilt: 0.0);
        var strongUp = Hot(31, "Normal", now, 0.4, boost, tilt: tilt, theta: theta, slope: 1.0);
        var strongDn = Hot(31, "Normal", now, 0.4, boost, tilt: tilt, theta: theta, slope: 0.0);
        Assert.True(strongUp >= noTilt);
        Assert.True(strongDn <= noTilt);
        Assert.InRange(strongUp, 1.0 / boost, boost);
        Assert.InRange(strongDn, 1.0 / boost, boost);
    }

    [Fact]
    public void Hot_DisabledService_CompositionSizeHotIsOne()
    {
        var stocks = new Mock<IStockService>();
        stocks.Setup(s => s.ById).Returns(new Dictionary<int, Stock>());
        var profiles  = new StockProfileService(enabled: false);
        var sentiment = new BotSentimentService(stocks.Object, profiles, NullLogger<BotSentimentService>.Instance);
        // Enabled activity but hot-rotation off (Boost default 1.0) ⇒ H≡1, byte-identical.
        var svc = new BotActivityService(stocks.Object, sentiment, NullLogger<BotActivityService>.Instance,
            recentReturn: _ => 0.0, enabled: true);
        Assert.False(svc.HotRotationEnabled);
        Assert.Equal(1.0, svc.CompositionSizeHot(10));
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
