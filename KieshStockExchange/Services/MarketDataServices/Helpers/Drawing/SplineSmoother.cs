using KieshStockExchange.Models.ChartDrawing.Objects;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// UP-CORE: Freehand/Brush smoothing primitives. Points (DrawPoint) are what persist — like a polyline —
// and the spline is re-evaluated at draw time, so nothing here mutates the stored data. Pure/headless.
//
// Both operate in the drawing's own data space (T seconds from the first point, P as the value). A
// rendering phase projects the resulting path to pixels (or pre-scales minPx); UP-CORE only ships the
// pure math + its unit tests — no renderer reaches it yet.
public static class SplineSmoother
{
    // Drops points that fall within minPx of the previously kept point (a capture-noise filter). The
    // first and last points are always kept so the stroke's extent is preserved.
    public static IReadOnlyList<DrawPoint> Decimate(IReadOnlyList<DrawPoint> pts, float minPx)
    {
        if (pts is null || pts.Count <= 2 || minPx <= 0f) return pts ?? Array.Empty<DrawPoint>();

        var kept = new List<DrawPoint>(pts.Count) { pts[0] };
        var origin = pts[0].T;
        var last = ToXy(pts[0], origin);
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var cur = ToXy(pts[i], origin);
            if (Dist(last, cur) >= minPx)
            {
                kept.Add(pts[i]);
                last = cur;
            }
        }
        kept.Add(pts[^1]);   // always keep the terminal point
        return kept;
    }

    // Builds a path through the points. tension 0 = follow the points exactly (straight segments);
    // higher tension = rounder Catmull-Rom curves. The curve always passes through every input point.
    public static PathF Evaluate(IReadOnlyList<DrawPoint> pts, float tension)
    {
        var path = new PathF();
        if (pts is null || pts.Count == 0) return path;

        var origin = pts[0].T;
        var p = new PointF[pts.Count];
        for (int i = 0; i < pts.Count; i++) p[i] = ToXy(pts[i], origin);

        path.MoveTo(p[0]);
        if (pts.Count == 1) return path;

        float t = Math.Clamp(tension, 0f, 1f);
        if (t <= 0f || pts.Count == 2)
        {
            // Exact polyline through the points.
            for (int i = 1; i < p.Length; i++) path.LineTo(p[i]);
            return path;
        }

        // Catmull-Rom → cubic Bézier. The tangent scale grows with tension (0 collapses to straight
        // segments, handled above; 1 gives the classic 1/6 Catmull-Rom handles).
        float k = t / 6f;
        for (int i = 0; i < p.Length - 1; i++)
        {
            PointF p0 = p[Math.Max(i - 1, 0)];
            PointF p1 = p[i];
            PointF p2 = p[i + 1];
            PointF p3 = p[Math.Min(i + 2, p.Length - 1)];

            var c1 = new PointF(p1.X + (p2.X - p0.X) * k, p1.Y + (p2.Y - p0.Y) * k);
            var c2 = new PointF(p2.X - (p3.X - p1.X) * k, p2.Y - (p3.Y - p1.Y) * k);
            path.CurveTo(c1, c2, p2);
        }
        return path;
    }

    private static PointF ToXy(DrawPoint dp, DateTime origin)
        => new((float)(dp.T - origin).TotalSeconds, (float)dp.P);

    private static float Dist(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
