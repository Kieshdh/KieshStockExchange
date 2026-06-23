using KieshStockExchange.ViewModels.PortfolioViewModels;

namespace KieshStockExchange.Helpers;

/// <summary>
/// Draws the Portfolio allocation donut as a series of stroked arcs along a
/// single ring, which avoids the "punch a hole in the middle" trick (that
/// only works against a known card-surface color).
/// </summary>
public sealed class PortfolioAllocationDrawable : IDrawable
{
    private IReadOnlyList<PortfolioAllocationSlice> _slices = Array.Empty<PortfolioAllocationSlice>();

    public void SetSlices(IReadOnlyList<PortfolioAllocationSlice> slices)
    {
        _slices = slices ?? Array.Empty<PortfolioAllocationSlice>();
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var side = Math.Min(dirtyRect.Width, dirtyRect.Height);
        var cx = dirtyRect.Center.X;
        var cy = dirtyRect.Center.Y;
        var outerRadius = side * 0.45f;
        var innerRadius = outerRadius * 0.55f;
        var ringWidth = outerRadius - innerRadius;
        var midRadius = (outerRadius + innerRadius) / 2f;

        canvas.StrokeLineCap = LineCap.Butt;
        canvas.StrokeSize = ringWidth;

        // Empty state (fresh / zero-equity account): draw a faint placeholder ring so the
        // card reads as "nothing to allocate yet" instead of a blank, broken-looking square.
        if (_slices.Count == 0)
        {
            canvas.StrokeColor = Color.FromRgba(128, 128, 128, 45);
            canvas.DrawCircle(cx, cy, midRadius);
            return;
        }

        // MAUI angles: 0° = east, 90° = north, counterclockwise positive.
        // Start the donut at 12 o'clock and walk clockwise so the visual
        // order matches the legend (top-down, largest first).
        double startAngle = 90;

        for (int i = 0; i < _slices.Count; i++)
        {
            var slice = _slices[i];
            var sweep = slice.Share * 360.0;
            if (sweep <= 0) continue;
            var endAngle = startAngle - sweep; // clockwise = decreasing

            canvas.StrokeColor = slice.Color;
            canvas.DrawArc(
                cx - midRadius, cy - midRadius,
                midRadius * 2, midRadius * 2,
                (float)startAngle, (float)endAngle,
                clockwise: true, closed: false);

            startAngle = endAngle;
        }
    }
}
