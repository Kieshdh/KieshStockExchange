namespace KieshStockExchange.Services.MarketDataServices.Helpers;

// A tiny IDrawable that paints ONE horizontal specimen of a DrawStyle: the exact colour + thickness +
// dash + line-ending. Hosted by the pen-tray toolbar button, the panel live-preview, and the width /
// dash / ending tiles — each a GraphicsView whose Drawable is one of these. Reassigning the instance
// (the pen VM rebuilds it on a style change) is what repaints the host, so the props are plain setters.
public sealed class StylePreviewDrawable : IDrawable
{
    public Color? Color { get; set; }
    public float Thickness { get; set; } = 1.5f;
    public DashKind Dash { get; set; } = DashKind.Solid;
    public LineEnding Ending { get; set; } = LineEnding.None;

    public void Draw(ICanvas canvas, RectF r)
    {
        var color = Color ?? Colors.Gray;
        float th = Thickness > 0f ? Thickness : 1.5f;
        float y = r.Center.Y;
        // Cap the head to the strip height so a bold ending never overruns a small tile.
        float size = Math.Min(12f + 3f * th, r.Height * 0.9f);
        float pad = 4f + size;
        float ax = r.Left + pad, bx = r.Right - pad;
        if (bx <= ax) { ax = r.Left + 2f; bx = r.Right - 2f; }

        canvas.StrokeColor = color;
        canvas.StrokeSize = th;
        canvas.StrokeDashPattern = DashPattern(Dash);
        canvas.DrawLine(ax, y, bx, y);
        canvas.StrokeDashPattern = null;

        // Horizontal specimen: forward = right (1,0) at both ends.
        DrawEndings(canvas, ax, y, 1f, 0f, bx, y, 1f, 0f, Ending, color, size);
    }

    // Solid = no pattern; Dash = medium dashes; Dot = tight dots (mirrors CandleChartDrawable).
    public static float[]? DashPattern(DashKind kind) => kind switch
    {
        DashKind.Dash => new[] { 5f, 4f },
        DashKind.Dot => new[] { 1f, 3f },
        _ => null,
    };

    // Paint the line-ending heads for a segment. start/end are the terminal points; startFwd/endFwd
    // are the forward (start→end) directions at each end (they differ only for a polyline, whose ends
    // follow their own segments). A head is drawn OUTWARD (reverse of forward) at the start for
    // Start/BothOut, and FORWARD for BothForward; the end head always points forward.
    public static void DrawEndings(ICanvas canvas,
        float startX, float startY, float startFwdX, float startFwdY,
        float endX, float endY, float endFwdX, float endFwdY,
        LineEnding ending, Color color, float size)
    {
        bool headAtEnd = ending is LineEnding.End or LineEnding.BothOut or LineEnding.BothForward;
        bool headAtStart = ending is LineEnding.Start or LineEnding.BothOut or LineEnding.BothForward;
        if (headAtEnd)
            DrawArrowHead(canvas, endX, endY, endFwdX, endFwdY, color, size);
        if (headAtStart)
        {
            bool forward = ending == LineEnding.BothForward;
            DrawArrowHead(canvas, startX, startY,
                forward ? startFwdX : -startFwdX, forward ? startFwdY : -startFwdY, color, size);
        }
    }

    // Filled triangle arrowhead with its tip at (tipX,tipY) pointing along (dirX,dirY); size = barb length.
    public static void DrawArrowHead(ICanvas canvas, float tipX, float tipY, float dirX, float dirY, Color color, float size)
    {
        float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
        if (len < 1e-4f) return;
        float ux = dirX / len, uy = dirY / len;   // unit direction
        float px = -uy, py = ux;                   // perpendicular
        float baseX = tipX - ux * size, baseY = tipY - uy * size;
        float half = size * 0.5f;
        var path = new PathF();
        path.MoveTo(tipX, tipY);
        path.LineTo(baseX + px * half, baseY + py * half);
        path.LineTo(baseX - px * half, baseY - py * half);
        path.Close();
        canvas.FillColor = color;
        canvas.FillPath(path);
    }
}
