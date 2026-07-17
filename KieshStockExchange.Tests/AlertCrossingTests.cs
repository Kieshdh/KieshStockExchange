using KieshStockExchange.Models.ChartDrawing.Objects;

namespace KieshStockExchange.Tests;

/// <summary>
/// AlertCrossing.Crossed decides whether a (lastPrice, newPrice) pair crossed a level per
/// AlertCondition. Boundary: touching the level completes the cross on the NEW sample — CrossUp
/// is last &lt; level &lt;= new, CrossDown is last &gt; level &gt;= new; starting exactly on the level
/// does not itself fire.
/// </summary>
public sealed class AlertCrossingTests
{
    private const decimal Level = 150m;

    // xunit InlineData can't take `decimal` literals (not a valid attribute-argument type per
    // CS0182) — pass `double` and convert at the call site instead.
    [Theory]
    [InlineData(149, 150)]   // touches the level from below
    [InlineData(149, 151)]   // passes straight through
    [InlineData(140, 200)]   // large upward jump spanning the level
    public void CrossUp_FiresOnUpwardCross(double last, double @new)
        => Assert.True(AlertCrossing.Crossed((decimal)last, (decimal)@new, Level, AlertCondition.CrossUp));

    [Theory]
    [InlineData(151, 150)]   // downward move — CrossUp must not fire
    [InlineData(151, 152)]   // stays above, no cross at all
    [InlineData(140, 145)]   // stays below, no cross at all
    public void CrossUp_DoesNotFireOnDownwardOrNoCross(double last, double @new)
        => Assert.False(AlertCrossing.Crossed((decimal)last, (decimal)@new, Level, AlertCondition.CrossUp));

    [Theory]
    [InlineData(151, 150)]   // touches the level from above
    [InlineData(151, 149)]   // passes straight through
    [InlineData(200, 140)]   // large downward jump spanning the level
    public void CrossDown_FiresOnDownwardCross(double last, double @new)
        => Assert.True(AlertCrossing.Crossed((decimal)last, (decimal)@new, Level, AlertCondition.CrossDown));

    [Theory]
    [InlineData(149, 150)]   // upward move — CrossDown must not fire
    [InlineData(149, 148)]   // stays below, no cross at all
    [InlineData(151, 155)]   // stays above, no cross at all
    public void CrossDown_DoesNotFireOnUpwardOrNoCross(double last, double @new)
        => Assert.False(AlertCrossing.Crossed((decimal)last, (decimal)@new, Level, AlertCondition.CrossDown));

    [Theory]
    [InlineData(149, 151)]   // upward cross
    [InlineData(151, 149)]   // downward cross
    [InlineData(149, 150)]   // touches from below
    [InlineData(151, 150)]   // touches from above
    public void CrossAny_FiresOnEitherDirection(double last, double @new)
        => Assert.True(AlertCrossing.Crossed((decimal)last, (decimal)@new, Level, AlertCondition.CrossAny));

    [Theory]
    [InlineData(140, 145)]   // stays below the level
    [InlineData(151, 155)]   // stays above the level
    [InlineData(150, 150)]   // sits exactly on the level, no movement
    public void CrossAny_DoesNotFireWhenPriceStaysOnOneSide(double last, double @new)
        => Assert.False(AlertCrossing.Crossed((decimal)last, (decimal)@new, Level, AlertCondition.CrossAny));

    [Fact]
    public void StartingExactlyOnLevel_DoesNotFireByItself()
    {
        // last == level: neither "< level" (CrossUp) nor "> level" (CrossDown) holds, so a
        // subsequent move away-and-back is required before the alert can fire again.
        Assert.False(AlertCrossing.Crossed(Level, 151m, Level, AlertCondition.CrossAny));
        Assert.False(AlertCrossing.Crossed(Level, 149m, Level, AlertCondition.CrossAny));
    }

    [Fact]
    public void TouchingLevelExactly_CountsAsTheCross_NotBothDirections()
    {
        // Landing exactly on the level from below fires CrossUp only, from above CrossDown only —
        // CrossAny still fires exactly once per pair, never double-counts a single touch.
        Assert.True(AlertCrossing.Crossed(149m, Level, Level, AlertCondition.CrossUp));
        Assert.False(AlertCrossing.Crossed(149m, Level, Level, AlertCondition.CrossDown));

        Assert.True(AlertCrossing.Crossed(151m, Level, Level, AlertCondition.CrossDown));
        Assert.False(AlertCrossing.Crossed(151m, Level, Level, AlertCondition.CrossUp));
    }
}
