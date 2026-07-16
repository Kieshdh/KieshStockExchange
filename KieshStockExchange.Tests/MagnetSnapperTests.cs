using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

namespace KieshStockExchange.Tests;

/// <summary>
/// UP-CORE — MagnetSnapper (headless drawing-anchor snapping). Covers the four rules from the spec:
/// Weak snaps only inside the 8px radius, Strong always snaps to the nearest candidate, no candidate
/// near the cursor passes through in both modes, and Off/None is a no-op the helper still handles.
/// </summary>
public sealed class MagnetSnapperTests
{
    private static SnapCandidate Cand(float x, float y, SnapCandidateKind k = SnapCandidateKind.CandleClose)
        => new(new PointF(x, y), k, DateTime.UnixEpoch, 0m);

    [Fact]
    public void Weak_WithinEightPx_Snaps()
    {
        var cands = new[] { Cand(100f, 100f) };
        var r = MagnetSnapper.Snap(new PointF(100f, 105f), MagnetMode.Weak, AxisMask.Y, cands);

        Assert.True(r.Snapped);
        Assert.Equal(100f, r.Point.Y, 3);          // snapped to the candidate's price pixel
        Assert.Equal(SnapCandidateKind.CandleClose, r.Kind);
    }

    [Fact]
    public void Weak_BeyondEightPx_PassesThrough()
    {
        var cands = new[] { Cand(100f, 100f) };
        var cursor = new PointF(100f, 110f);        // 10px away > 8px threshold
        var r = MagnetSnapper.Snap(cursor, MagnetMode.Weak, AxisMask.Y, cands);

        Assert.False(r.Snapped);
        Assert.Equal(cursor, r.Point);
    }

    [Fact]
    public void Strong_AlwaysSnaps_EvenFarAway()
    {
        var cands = new[] { Cand(100f, 100f), Cand(100f, 300f) };
        var r = MagnetSnapper.Snap(new PointF(100f, 120f), MagnetMode.Strong, AxisMask.Y, cands);

        Assert.True(r.Snapped);
        Assert.Equal(100f, r.Point.Y, 3);          // nearest of the two candidates
    }

    [Fact]
    public void NoCandidates_PassesThrough_BothModes()
    {
        var cursor = new PointF(42f, 42f);
        Assert.False(MagnetSnapper.Snap(cursor, MagnetMode.Weak, AxisMask.Both, Array.Empty<SnapCandidate>()).Snapped);
        Assert.False(MagnetSnapper.Snap(cursor, MagnetMode.Strong, AxisMask.Both, Array.Empty<SnapCandidate>()).Snapped);
    }

    [Fact]
    public void OffMode_IsNoOp()
    {
        var cands = new[] { Cand(100f, 100f) };
        var cursor = new PointF(100f, 100f);        // right on the candidate, but mode is Off
        var r = MagnetSnapper.Snap(cursor, MagnetMode.Off, AxisMask.Both, cands);

        Assert.False(r.Snapped);
        Assert.Equal(cursor, r.Point);
    }

    [Fact]
    public void AxisMaskX_SnapsX_KeepsCursorY()
    {
        var cands = new[] { Cand(100f, 100f) };
        var r = MagnetSnapper.Snap(new PointF(103f, 250f), MagnetMode.Strong, AxisMask.X, cands);

        Assert.True(r.Snapped);
        Assert.Equal(100f, r.Point.X, 3);           // X snapped to candidate
        Assert.Equal(250f, r.Point.Y, 3);           // Y left at the cursor
    }
}
