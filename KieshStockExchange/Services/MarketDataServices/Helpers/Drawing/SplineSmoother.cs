namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Freehand stroke simplification. A freehand drag captures a DENSE pixel path; this reduces it to the
// curvature-significant points (Ramer–Douglas–Peucker) so the render's B-spline (see
// CandleChartDrawable.DrawFreehandPath) gets clean control points — sparse on straight runs, dense
// through curves. Pure/headless; operates in PIXEL space so the tolerance is a visible on-screen distance.
public static class SplineSmoother
{
    // RDP: returns the ASCENDING indices of the points to keep so the simplified polyline stays within
    // tolerancePx of the original. Endpoints are always kept; tolerancePx <= 0 (or <= 2 points) keeps all.
    public static IReadOnlyList<int> SimplifyRdp(IReadOnlyList<PointF> pts, float tolerancePx)
    {
        int n = pts?.Count ?? 0;
        if (n <= 2 || tolerancePx <= 0f) return AllIndices(n);

        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;

        // Iterative divide-and-conquer (an explicit stack avoids deep recursion on long strokes): for each
        // segment keep the point farthest from its chord if it exceeds the tolerance, then split there.
        var stack = new Stack<(int lo, int hi)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (lo, hi) = stack.Pop();
            if (hi <= lo + 1) continue;   // no interior points

            float max = 0f;
            int farthest = -1;
            for (int i = lo + 1; i < hi; i++)
            {
                float d = PerpendicularDistance(pts[i], pts[lo], pts[hi]);
                if (d > max) { max = d; farthest = i; }
            }
            if (max > tolerancePx && farthest > lo)
            {
                keep[farthest] = true;
                stack.Push((lo, farthest));
                stack.Push((farthest, hi));
            }
        }

        var kept = new List<int>(n);
        for (int i = 0; i < n; i++) if (keep[i]) kept.Add(i);
        return kept;
    }

    // Perpendicular distance from p to the chord a→b (degenerate a==b ⇒ point distance |p-a|).
    private static float PerpendicularDistance(PointF p, PointF a, PointF b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 <= 1e-6f) return Dist(p, a);
        float cross = dx * (p.Y - a.Y) - dy * (p.X - a.X);   // |(b-a) × (p-a)| / |b-a|
        return MathF.Abs(cross) / MathF.Sqrt(len2);
    }

    private static IReadOnlyList<int> AllIndices(int n)
    {
        var all = new List<int>(n);
        for (int i = 0; i < n; i++) all.Add(i);
        return all;
    }

    private static float Dist(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
