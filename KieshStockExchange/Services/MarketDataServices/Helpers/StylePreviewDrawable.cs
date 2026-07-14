namespace KieshStockExchange.Services.MarketDataServices.Helpers;

// How a StylePreviewDrawable paints its tile. Line = a horizontal line honouring dash + endings/head
// (the live preview + ending/head tiles). Dot = a centred filled circle sized by thickness (the WIDTH
// tiles). Dash = a near-edge-to-edge thick line so the solid/dash/dot pattern reads (the DASH tiles).
public enum StylePreviewMode { Line, Dot, Dash }

// A tiny IDrawable that paints ONE specimen of a DrawStyle: the exact colour + thickness + dash +
// line-ending + head shape. Hosted by the panel live-preview and the width / dash / ending / head
// tiles — each a GraphicsView whose Drawable is one of these. Reassigning the instance (the pen VM
// rebuilds it on a style change) is what repaints the host, so the props are plain setters.
public sealed class StylePreviewDrawable : IDrawable
{
    public Color? Color { get; set; }
    public float Thickness { get; set; } = 1.5f;
    public DashKind Dash { get; set; } = DashKind.Solid;
    public LineEnding Ending { get; set; } = LineEnding.None;
    public ArrowHeadStyle Head { get; set; } = ArrowHeadStyle.FilledTriangle;
    public StylePreviewMode Mode { get; set; } = StylePreviewMode.Line;

    public void Draw(ICanvas canvas, RectF r)
    {
        var color = Color ?? Colors.Gray;
        float th = Thickness > 0f ? Thickness : 1.5f;
        float y = r.Center.Y;

        if (Mode == StylePreviewMode.Dot)
        {
            // WIDTH tile: a centred filled dot whose radius scales with thickness (×3, capped to the
            // tile) so the three widths read as clearly small / medium / large.
            float radius = Math.Min(th * 3f, Math.Min(r.Width, r.Height) * 0.42f);
            canvas.FillColor = color;
            canvas.FillCircle(r.Center.X, y, radius);
            return;
        }

        if (Mode == StylePreviewMode.Dash)
        {
            // DASH tile: a near-full-width line at a clearly-visible thickness so solid vs dash vs dot
            // is obvious across the whole tile.
            float dax = r.Left + r.Width * 0.04f, dbx = r.Right - r.Width * 0.04f;
            canvas.StrokeColor = color;
            canvas.StrokeSize = Math.Max(2.5f, th);
            canvas.StrokeDashPattern = DashPattern(Dash);
            canvas.DrawLine(dax, y, dbx, y);
            canvas.StrokeDashPattern = null;
            return;
        }

        // Line mode (live preview + ending/head tiles). A near-full-width straight specimen; the head
        // barb is capped by the strip height and ~30% of its width. DrawStraightSegment stops the line
        // at each head's base so heads never overlap and the hollow head reads clean.
        float ax = r.Left + 3f, bx = r.Right - 3f;
        if (bx <= ax) { ax = r.Left + 1f; bx = r.Right - 1f; }
        float size = Math.Min(Math.Min(12f + 3f * th, r.Height * 0.9f), r.Width * 0.30f);
        DrawStraightSegment(canvas, ax, y, bx, y, Ending, color, th, DashPattern(Dash), Head, size);
    }

    // Solid = no pattern; Dash = medium dashes; Dot = tight dots (mirrors CandleChartDrawable).
    public static float[]? DashPattern(DashKind kind) => kind switch
    {
        DashKind.Dash => new[] { 5f, 4f },
        DashKind.Dot => new[] { 1f, 3f },
        _ => null,
    };

    // Draw a straight dashed segment a→b with its line-endings, terminating the connecting LINE at each
    // present head's BASE (not the tip). This keeps the hollow/open heads clean (no line through their
    // interior) and, for a double-ended ending, clamps each head to ≤30% of the segment so a run of line
    // stays visible between the two heads (no overlap). Shared by every straight drawing + the specimen.
    public static void DrawStraightSegment(ICanvas canvas,
        float ax, float ay, float bx, float by,
        LineEnding ending, Color color, float thickness, float[]? dashPattern,
        ArrowHeadStyle head, float headSize)
    {
        float dx = bx - ax, dy = by - ay;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return;
        float ux = dx / len, uy = dy / len;

        bool headStart = ending is LineEnding.Start or LineEnding.BothOut;  // outward head at the start
        bool headEnd = ending is LineEnding.End or LineEnding.BothOut or LineEnding.BothForward;

        float eff = headSize;
        if (headStart && headEnd) eff = Math.Min(headSize, len * 0.30f);
        eff = Math.Min(eff, len);   // never longer than the segment itself

        // The line stops at the BASE of any head pointing inward along the segment: an outward start
        // head (base at +u) and a forward end head (base at −u).
        float startCut = headStart ? eff : 0f;
        float endCut = headEnd ? eff : 0f;
        float lax = ax + ux * startCut, lay = ay + uy * startCut;
        float lbx = bx - ux * endCut, lby = by - uy * endCut;

        canvas.StrokeColor = color;
        canvas.StrokeSize = thickness;
        canvas.StrokeDashPattern = dashPattern;
        if (lax != lbx || lay != lby) canvas.DrawLine(lax, lay, lbx, lby);
        canvas.StrokeDashPattern = null;

        DrawEndings(canvas, ax, ay, ux, uy, bx, by, ux, uy, ending, color, eff, head, thickness);
    }

    // Paint the line-ending heads for a segment. start/end are the terminal points; startFwd/endFwd
    // are the forward (start→end) directions at each end (they differ only for a polyline, whose ends
    // follow their own segments). A head is drawn OUTWARD (reverse of forward) at the start for
    // Start/BothOut, and FORWARD for BothForward; the end head always points forward. head/strokeWidth
    // select the head shape (see DrawArrowHead).
    public static void DrawEndings(ICanvas canvas,
        float startX, float startY, float startFwdX, float startFwdY,
        float endX, float endY, float endFwdX, float endFwdY,
        LineEnding ending, Color color, float size, ArrowHeadStyle head, float strokeWidth)
    {
        bool headAtEnd = ending is LineEnding.End or LineEnding.BothOut or LineEnding.BothForward;
        bool headAtStart = ending is LineEnding.Start or LineEnding.BothOut or LineEnding.BothForward;
        if (headAtEnd)
            DrawArrowHead(canvas, endX, endY, endFwdX, endFwdY, color, size, head, strokeWidth);
        if (headAtStart)
        {
            bool forward = ending == LineEnding.BothForward;
            DrawArrowHead(canvas, startX, startY,
                forward ? startFwdX : -startFwdX, forward ? startFwdY : -startFwdY,
                color, size, head, strokeWidth);
        }
    }

    // A head at (tipX,tipY) pointing along (dirX,dirY): FilledTriangle = a solid barbed triangle; Open =
    // two barb strokes forming a hollow "V"; Outline = the same triangle stroked as an outline, no fill.
    public static void DrawArrowHead(ICanvas canvas, float tipX, float tipY, float dirX, float dirY,
        Color color, float size, ArrowHeadStyle head, float strokeWidth)
    {
        float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
        if (len < 1e-4f) return;
        float ux = dirX / len, uy = dirY / len;   // unit direction
        float px = -uy, py = ux;                   // perpendicular
        float baseX = tipX - ux * size, baseY = tipY - uy * size;
        float half = size * 0.5f;
        float cx1 = baseX + px * half, cy1 = baseY + py * half;
        float cx2 = baseX - px * half, cy2 = baseY - py * half;

        if (head == ArrowHeadStyle.Open)
        {
            canvas.StrokeColor = color;
            canvas.StrokeSize = Math.Max(1.5f, strokeWidth);
            canvas.StrokeLineCap = LineCap.Round;
            canvas.DrawLine(tipX, tipY, cx1, cy1);
            canvas.DrawLine(tipX, tipY, cx2, cy2);
            canvas.StrokeLineCap = LineCap.Butt;   // don't leak the cap into later strokes
            return;
        }

        var path = new PathF();
        path.MoveTo(tipX, tipY);
        path.LineTo(cx1, cy1);
        path.LineTo(cx2, cy2);
        path.Close();
        if (head == ArrowHeadStyle.Outline)
        {
            canvas.StrokeColor = color;
            canvas.StrokeSize = Math.Max(1.5f, strokeWidth);
            canvas.StrokeLineJoin = LineJoin.Miter;
            canvas.DrawPath(path);
        }
        else   // FilledTriangle
        {
            canvas.FillColor = color;
            canvas.FillPath(path);
        }
    }
}
