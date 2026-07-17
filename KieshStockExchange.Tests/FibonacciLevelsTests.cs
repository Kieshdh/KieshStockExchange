using KieshStockExchange.Models.ChartDrawing.Objects;

namespace KieshStockExchange.Tests;

/// <summary>
/// FibonacciLevels maps ratios to prices by linear interpolation between two anchors: ratio 0 lands
/// exactly on P1, ratio 1 exactly on P2 (regardless of ratio precision), ratios in (0,1) retrace the
/// move, ratios &gt;1 extend past P2, and the default grid has the classic 12 ratios in order.
/// </summary>
public sealed class FibonacciLevelsTests
{
    [Fact]
    public void Price_OnAnchors_IsExact()
    {
        const decimal p1 = 123.456789m, p2 = 987.654321m;
        Assert.Equal(p1, FibonacciLevels.Price(p1, p2, 0.0));
        Assert.Equal(p2, FibonacciLevels.Price(p1, p2, 1.0));
    }

    [Fact]
    public void Price_Midpoint_IsAverage()
        => Assert.Equal(150m, FibonacciLevels.Price(100m, 200m, 0.5));

    [Fact]
    public void Price_Retracement_InterpolatesUpMove()
        => Assert.Equal(161.8m, FibonacciLevels.Price(100m, 200m, 0.618));

    [Fact]
    public void Price_Extension_ProjectsPastP2()
        => Assert.Equal(261.8m, FibonacciLevels.Price(100m, 200m, 1.618));

    [Fact]
    public void Price_DownMove_RetracesFromTheOtherSide()
        => Assert.Equal(161.8m, FibonacciLevels.Price(200m, 100m, 0.382));

    [Fact]
    public void Levels_DefaultGrid_HasTwelveRatiosInOrder()
    {
        var levels = FibonacciLevels.Levels(100m, 200m);
        Assert.Equal(12, levels.Count);
        Assert.Equal(0.0, levels[0].Ratio);
        Assert.Equal(100m, levels[0].Price);
        Assert.Equal(2.618, levels[^1].Ratio);
        Assert.Equal(361.8m, levels[^1].Price);
    }

    [Fact]
    public void Levels_CustomRatios_PreservedInOrder()
    {
        var levels = FibonacciLevels.Levels(100m, 200m, new[] { 0.5, 0.25 });
        Assert.Equal(new[] { 0.5, 0.25 }, levels.Select(l => l.Ratio));
        Assert.Equal(new[] { 150m, 125m }, levels.Select(l => l.Price));
    }
}
