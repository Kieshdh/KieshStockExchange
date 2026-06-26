using System.Collections.Generic;
using System.Linq;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §co-movement determinism: the shared market-factor lever drives cross-stock co-movement (return
/// correlation, ~0 today) via ONE shared bounded walk scaled by a per-stock beta. The beta must be a
/// pure, stable, positive function of stockId — no RNG, no reseed — so the dispersion is reproducible
/// and runtime-only. The shared walk itself reuses <see cref="BotMath.SoftWallStep"/> (covered by the
/// RegimeDrift/soft-wall tests); here we pin the beta surface. Mirrors ImpactDecoupleDeterminismTests.
/// </summary>
public class CoMovementDeterminismTests
{
    [Fact]
    public void Beta_is_deterministic_per_stock()
    {
        for (int id = 1; id <= 50; id++)
            Assert.Equal(BotSentimentService.CoMoveBeta(id, 0.4), BotSentimentService.CoMoveBeta(id, 0.4));
    }

    [Fact]
    public void Beta_zero_spread_is_exactly_one()
    {
        // spread 0 ⇒ every stock loads identically (beta 1.0) ⇒ pure common-mode, no dispersion.
        for (int id = 1; id <= 50; id++)
            Assert.Equal(1.0, BotSentimentService.CoMoveBeta(id, 0.0));
    }

    [Fact]
    public void Beta_is_positive_and_within_band()
    {
        const double spread = 0.4;
        for (int id = 1; id <= 200; id++)
        {
            double b = BotSentimentService.CoMoveBeta(id, spread);
            Assert.True(b > 0.0, $"beta must stay positive (id={id}, b={b})");
            Assert.InRange(b, 0.05, 1.0 + spread); // 1 ± spread, positive-clamped at 0.05
        }
    }

    [Fact]
    public void Beta_disperses_across_stocks_centered_near_one()
    {
        const double spread = 0.4;
        var betas = Enumerable.Range(1, 50).Select(id => BotSentimentService.CoMoveBeta(id, spread)).ToList();
        Assert.True(betas.Max() - betas.Min() > 0.2, "betas should disperse across stocks (realistic beta spread)");
        Assert.InRange(betas.Average(), 0.85, 1.15); // ~uniform hash ⇒ mean beta near 1.0
    }

    [Fact]
    public void Beta_wider_spread_widens_dispersion()
    {
        double Range(double spread)
        {
            var bs = Enumerable.Range(1, 50).Select(id => BotSentimentService.CoMoveBeta(id, spread)).ToList();
            return bs.Max() - bs.Min();
        }
        Assert.True(Range(0.6) > Range(0.2), "wider spread ⇒ wider beta band");
    }
}
