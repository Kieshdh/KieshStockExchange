using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

namespace KieshStockExchange.Tests;

/// <summary>
/// UP-CORE — SplineSmoother: decimation drops points closer than minPx (endpoints always kept), and
/// Evaluate at tension 0 produces a polyline that passes through every input point.
/// </summary>
public sealed class SplineSmootherTests
{
    // All points share the origin time, so ToXy maps them to X=0 and Y=P — pixel distance == |ΔP|.
    private static DrawPoint P(decimal price) => new(DateTime.UnixEpoch, price);

    [Fact]
    public void Decimate_DropsSubMinPxPoint_KeepsEndpoints()
    {
        var pts = new[] { P(0m), P(0.5m), P(5m) };   // middle point is 0.5px from the first
        var kept = SplineSmoother.Decimate(pts, minPx: 1f);

        Assert.Equal(2, kept.Count);                 // the near middle point is dropped
        Assert.Equal(0m, kept[0].P);
        Assert.Equal(5m, kept[^1].P);                // terminal point preserved
    }

    [Fact]
    public void Decimate_KeepsPointsBeyondThreshold()
    {
        var pts = new[] { P(0m), P(3m), P(6m) };     // every gap is 3px >= minPx
        var kept = SplineSmoother.Decimate(pts, minPx: 1f);

        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void Evaluate_TensionZero_PassesThroughPoints()
    {
        var pts = new[] { P(0m), P(10m), P(4m) };
        var path = SplineSmoother.Evaluate(pts, tension: 0f);

        // A MoveTo + LineTo per subsequent point ⇒ one path point per input point, in order.
        Assert.Equal(pts.Length, path.Count);
        Assert.Equal(0f, path[0].Y, 3);
        Assert.Equal(10f, path[1].Y, 3);
        Assert.Equal(4f, path[2].Y, 3);
    }

    [Fact]
    public void Evaluate_Empty_ReturnsEmptyPath()
    {
        var path = SplineSmoother.Evaluate(Array.Empty<DrawPoint>(), tension: 0.5f);
        Assert.Equal(0, path.Count);
    }
}
