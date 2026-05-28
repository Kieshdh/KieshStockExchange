using KieshStockExchange.ViewModels.PortfolioViewModels;

namespace KieshStockExchange.Helpers;

/// <summary>
/// Draws the Portfolio allocation pie. Slices come from
/// <see cref="PortfolioAllocationSlice.Share"/> (0..1); colors and labels
/// are owned by the slice so the legend control can render them too.
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
        if (_slices.Count == 0) return;

        // Square inscribed in the rect, centered.
        var side = Math.Min(dirtyRect.Width, dirtyRect.Height);
        var cx = dirtyRect.Center.X;
        var cy = dirtyRect.Center.Y;
        var radius = side * 0.45f;

        // Donut hole keeps the slice arcs readable when shares get small.
        var holeRadius = radius * 0.45f;

        double startAngle = -90; // 12 o'clock
        for (int i = 0; i < _slices.Count; i++)
        {
            var slice = _slices[i];
            var sweep = slice.Share * 360.0;
            if (sweep <= 0) continue;

            canvas.FillColor = slice.Color;
            // ICanvas.FillArc draws a wedge from center; donut cutout below.
            canvas.FillArc(
                cx - radius, cy - radius, radius * 2, radius * 2,
                (float)startAngle, (float)(startAngle + sweep), clockwise: false);
            startAngle += sweep;
        }

        // Donut cutout — fill with the background color so the inner ring
        // reads as empty regardless of theme.
        canvas.FillColor = Colors.Transparent;
        // Workaround: ICanvas has no "subtract" but we can punch a hole by
        // filling a solid disc in a neutral color matching the card surface.
        canvas.FillColor = Color.FromArgb("#1F2937"); // dashboard card surface
        canvas.FillEllipse(cx - holeRadius, cy - holeRadius, holeRadius * 2, holeRadius * 2);
    }
}
