using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// Hybrid pressure formula §: pin the math of <see cref="AiBotDecisionService.BuyProbHybrid"/> —
/// the buyProb kernel that composes the homeostatic personality baseline with directional / herd
/// / anchor terms. Mirrors the pure-static style of CashHomeostasisTests / DirectionalBiasTests.
/// </summary>
public class PressureFormulaTests
{
    private const decimal Gain = 1.5m;

    // Helper aliases.
    private static decimal Add(decimal h, decimal d, decimal n, decimal hd, decimal a) =>
        AiBotDecisionService.BuyProbHybrid(h, d, n, hd, a, multiplicative: false, diversityGain: Gain);
    private static decimal Mul(decimal h, decimal d, decimal n, decimal hd, decimal a) =>
        AiBotDecisionService.BuyProbHybrid(h, d, n, hd, a, multiplicative: true, diversityGain: Gain);
    private static decimal Clamp01(decimal x) => x < 0m ? 0m : x > 1m ? 1m : x;

    [Fact]
    public void Flag_off_matches_additive_exactly()
    {
        // Representative table — pin the byte-for-byte equality between BuyProbHybrid(off) and
        // today's literal expression. This is the byte-identical-when-off guarantee, formalized.
        (decimal h, decimal d, decimal n, decimal hd, decimal a)[] cases = new[]
        {
            (0.50m,  0.00m, 1.00m,  0.00m,  0.00m),
            (0.60m,  0.20m, 1.00m,  0.05m,  0.10m),
            (0.40m, -0.30m, 0.70m,  0.00m, -0.05m),
            (0.10m,  0.50m, 1.00m,  0.10m, -0.30m),
            (0.90m, -0.50m, 0.50m, -0.10m,  0.20m),
            (0.50m,  0.80m, 1.00m,  0.00m,  0.00m),     // pre-clamp > 1
            (0.50m, -0.80m, 1.00m,  0.00m,  0.00m),     // pre-clamp < 0
        };
        foreach (var c in cases)
        {
            var expected = Clamp01(c.h + c.d * c.n + c.hd + c.a);
            Assert.Equal(expected, Add(c.h, c.d, c.n, c.hd, c.a));
        }
    }

    [Fact]
    public void Diversity_preserved_under_saturation()
    {
        // Strong buy directional under additive saturates BOTH bots to 1.0 (cohort spread = 0).
        // The multiplicative form keeps the spread alive — the structural property the design
        // doc's design-§4 claims as "≥2× the std deviation of additive."
        var addBuyBiased  = Add(0.6m, 0.6m, 1m, 0m, 0m);
        var addSellBiased = Add(0.4m, 0.6m, 1m, 0m, 0m);
        Assert.Equal(1m, addBuyBiased);
        Assert.Equal(1m, addSellBiased);
        var addSpread = Math.Abs(addBuyBiased - addSellBiased);
        Assert.Equal(0m, addSpread);

        var mulBuyBiased  = Mul(0.6m, 0.6m, 1m, 0m, 0m);
        var mulSellBiased = Mul(0.4m, 0.6m, 1m, 0m, 0m);
        // Hand-computed: f = 1 + 0.6·1.5 = 1.9; 0.5 ± 0.1·1.9 = {0.69, 0.31}.
        Assert.Equal(0.69m, mulBuyBiased);
        Assert.Equal(0.31m, mulSellBiased);
        var mulSpread = Math.Abs(mulBuyBiased - mulSellBiased);
        Assert.True(mulSpread > addSpread * 2m + 0.1m,
            $"multiplicative spread {mulSpread} should dwarf additive's {addSpread} under saturation");
    }

    [Fact]
    public void Sell_side_spread_mirrors_buy_side_spread()
    {
        // Q2 symmetric-around-0.5 property: cohort spread is invariant under the sign of
        // directional (no asymmetry between rallies and selloffs).
        var dirs = new[] { 0.2m, 0.4m, 0.6m, 0.8m };
        foreach (var d in dirs)
        {
            var buySideSpread  = Math.Abs(Mul(0.6m,  d, 1m, 0m, 0m) - Mul(0.4m,  d, 1m, 0m, 0m));
            var sellSideSpread = Math.Abs(Mul(0.6m, -d, 1m, 0m, 0m) - Mul(0.4m, -d, 1m, 0m, 0m));
            Assert.Equal(buySideSpread, sellSideSpread);
        }
    }

    [Fact]
    public void Anchor_overrides_personality_at_extremes()
    {
        // A large negative anchor forces buyProb to 0 regardless of homeostatic / directional —
        // the structural-override property anchors are designed for, under BOTH formulas
        // (anchors are additive in both).
        Assert.Equal(0m, Add(1.0m, 0.5m, 1m, 0m, -2.0m));
        Assert.Equal(0m, Mul(1.0m, 0.5m, 1m, 0m, -2.0m));
        // And the symmetric case: a large positive anchor floors buyProb at 1.
        Assert.Equal(1m, Add(0.0m, -0.5m, 1m, 0m, 2.0m));
        Assert.Equal(1m, Mul(0.0m, -0.5m, 1m, 0m, 2.0m));
    }

    [Fact]
    public void Neutral_bot_is_unmoved_by_multiplier()
    {
        // The most-important invariant of the symmetric-around-0.5 multiplicative form:
        // (homeostatic − 0.5) × f = 0 when homeostatic = 0.5, so any directional / herd /
        // diversity-gain combination collapses to buyProb = Clamp01(0.5 + anchor). The multiplier
        // cannot push a no-opinion bot in any direction.
        var dirs  = new[] { -0.8m, -0.3m, 0m, 0.3m, 0.8m };
        var herds = new[] { -0.2m, 0m, 0.2m };
        var anchs = new[] { -0.3m, -0.05m, 0m, 0.05m, 0.3m };
        foreach (var d in dirs)
        foreach (var hd in herds)
        foreach (var a in anchs)
        {
            var expected = Clamp01(0.5m + a);
            Assert.Equal(expected, Mul(0.5m, d, 1m, hd, a));
        }
    }
}
