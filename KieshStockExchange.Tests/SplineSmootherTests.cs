using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;
using Microsoft.Maui.Graphics;

namespace KieshStockExchange.Tests;

/// <summary>
/// SplineSmoother.SimplifyRdp reduces a dense pixel stroke to its curvature-significant points
/// (Ramer–Douglas–Peucker): a collinear run collapses to its endpoints, a corner beyond the tolerance
/// is kept, a corner within it is dropped, and short strokes are returned whole.
/// </summary>
public sealed class SplineSmootherTests
{
    private static int[] Simplify(PointF[] pts, float tolerancePx)
        => SplineSmoother.SimplifyRdp(pts, tolerancePx).ToArray();

    [Fact]
    public void SimplifyRdp_CollinearRun_KeepsOnlyEndpoints()
    {
        var pts = new[] { new PointF(0, 0), new PointF(1, 0), new PointF(2, 0), new PointF(3, 0), new PointF(4, 0) };
        Assert.Equal(new[] { 0, 4 }, Simplify(pts, tolerancePx: 1f));
    }

    [Fact]
    public void SimplifyRdp_KeepsCornerBeyondTolerance()
    {
        // The middle point sits 5px off the chord from first→last ⇒ kept.
        var pts = new[] { new PointF(0, 0), new PointF(2, 5), new PointF(4, 0) };
        Assert.Equal(new[] { 0, 1, 2 }, Simplify(pts, tolerancePx: 1f));
    }

    [Fact]
    public void SimplifyRdp_DropsCornerWithinTolerance()
    {
        // Same corner but only 0.5px off the chord ⇒ within tolerance ⇒ dropped.
        var pts = new[] { new PointF(0, 0), new PointF(2, 0.5f), new PointF(4, 0) };
        Assert.Equal(new[] { 0, 2 }, Simplify(pts, tolerancePx: 1f));
    }

    [Fact]
    public void SimplifyRdp_TwoOrFewer_KeepsAll()
    {
        var pts = new[] { new PointF(0, 0), new PointF(10, 10) };
        Assert.Equal(new[] { 0, 1 }, Simplify(pts, tolerancePx: 100f));
    }
}
