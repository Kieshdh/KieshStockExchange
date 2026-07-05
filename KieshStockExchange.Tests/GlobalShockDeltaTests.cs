using System;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §global-shock determinism: the discrete MARKET-WIDE bearish event's signed-magnitude draw. DownBias sets
/// P(bearish) (the sign), magnitude is min+span·U^exp (exp&gt;1 crowds toward the floor — many small, few big).
/// The CORRELATION property — a fired shock is ONE scalar added to EVERY stock's combined sentiment (see
/// BotSentimentService.Tick: <c>sum = _globalSum + _globalShock</c>) — is structural, so all stocks shift by
/// exactly the same amount; here we pin the pure draw. Mirrors BearShortBoostTests / CoMovementDeterminismTests
/// (no full-service construction — that does disk I/O + heavy mocks, per the existing test convention).
/// </summary>
public class GlobalShockDeltaTests
{
    const double Min = 0.3, Max = 1.5, Exp = 3.0;

    [Fact]
    public void DownBias_one_is_always_bearish()
    {
        // DownBias=1.0 ⇒ every event is a crash (negative delta), for any sign draw.
        for (double u = 0.0; u < 1.0; u += 0.1)
            Assert.True(BotSentimentService.GlobalShockDelta(u, 0.5, Min, Max, Exp, 1.0) < 0.0);
    }

    [Fact]
    public void DownBias_zero_is_always_bullish()
    {
        for (double u = 0.0; u < 1.0; u += 0.1)
            Assert.True(BotSentimentService.GlobalShockDelta(u, 0.5, Min, Max, Exp, 0.0) > 0.0);
    }

    [Fact]
    public void DownBias_splits_sign_at_the_threshold()
    {
        // signUniform below DownBias ⇒ bearish (−); at/above ⇒ bullish (+).
        Assert.True(BotSentimentService.GlobalShockDelta(0.50, 0.5, Min, Max, Exp, 0.85) < 0.0);
        Assert.True(BotSentimentService.GlobalShockDelta(0.90, 0.5, Min, Max, Exp, 0.85) > 0.0);
    }

    [Fact]
    public void Magnitude_stays_within_min_max()
    {
        for (double mu = 0.0; mu <= 1.0; mu += 0.05)
        {
            double d = Math.Abs(BotSentimentService.GlobalShockDelta(0.0, mu, Min, Max, Exp, 1.0));
            Assert.InRange(d, Min - 1e-9, Max + 1e-9);
        }
    }

    [Fact]
    public void Exponent_crowds_magnitude_toward_the_floor()
    {
        // exp>1 ⇒ the mid-uniform draw lands well below the linear midpoint (many small events, few big).
        double linMid = (Min + Max) / 2.0;
        double magAtHalf = Math.Abs(BotSentimentService.GlobalShockDelta(0.0, 0.5, Min, Max, Exp, 1.0));
        Assert.True(magAtHalf < linMid, $"exp>1 should crowd toward the floor (got {magAtHalf}, linMid {linMid})");
    }

    [Fact]
    public void Zero_span_is_exactly_the_floor()
    {
        // Degenerate guard: max==min ⇒ magnitude is exactly the floor (no NaN / negative span), sign from DownBias.
        Assert.Equal(-0.3, BotSentimentService.GlobalShockDelta(0.0, 0.7, 0.3, 0.3, Exp, 1.0), 12);
    }
}
