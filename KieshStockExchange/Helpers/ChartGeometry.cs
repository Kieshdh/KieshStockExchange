using Microsoft.Maui.Graphics;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.Helpers;

// Pure, stateless chart geometry + tick math shared by CandleChartDrawable's render pass and
// hit-testing (Arc-2 §4.1). Every member is a pure function — same inputs, same outputs — moved
// verbatim out of the drawable partials / RenderFrame so the visible shapes and their clickable
// zones keep one source of truth.
internal static class ChartGeometry
{
    public static float Dist(float ax, float ay, float bx, float by)
        => (float)Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    // Bounding rect of two corners. When square (a perfect Circle), the box is forced square — side = the
    // larger dimension, anchored at the top-left of the drag bbox — so render + hit-test agree.
    public static RectF ShapeRect(bool square, float x1, float y1, float x2, float y2)
    {
        float left = Math.Min(x1, x2), top = Math.Min(y1, y2);
        float w = Math.Abs(x2 - x1), h = Math.Abs(y2 - y1);
        if (square) { float s = Math.Max(w, h); w = s; h = s; }
        return new RectF(left, top, w, h);
    }

    // Shortest distance from point (px,py) to the segment (ax,ay)-(bx,by).
    public static float PointSegDist(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        if (len2 <= 1e-6f) return Dist(px, py, ax, ay);
        float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0f, 1f);
        return Dist(px, py, ax + t * dx, ay + t * dy);
    }

    // Far intersection of the ray (origin + t·dir, t ≥ 0) with the plot rect — the point where the
    // ray leaves the box. Origin is assumed inside; returns origin when the direction is degenerate.
    public static (float x, float y) RayExit(float ox, float oy, float dx, float dy, RectF r)
    {
        float t = float.MaxValue;
        if (dx > 1e-6f) t = Math.Min(t, (r.Right - ox) / dx);
        else if (dx < -1e-6f) t = Math.Min(t, (r.Left - ox) / dx);
        if (dy > 1e-6f) t = Math.Min(t, (r.Bottom - oy) / dy);
        else if (dy < -1e-6f) t = Math.Min(t, (r.Top - oy) / dy);
        if (t == float.MaxValue) t = 0f;
        return (ox + dx * t, oy + dy * t);
    }

    // Bigger, thickness-scaled line-ending head (3 px → ~21 px); arrowhead geometry itself lives on
    // StylePreviewDrawable so the chart and the pen-tray specimens draw identical heads.
    public static float EndSize(float thickness) => 12f + 3f * thickness;

    // Solid = no pattern; Dash = medium dashes; Dot = tight dots.
    public static float[]? DashPattern(DashKind kind) => kind switch
    {
        DashKind.Dash => new[] { 5f, 4f },
        DashKind.Dot => new[] { 1f, 3f },
        _ => null,
    };

    // A 7-vertex filled block arrow from tail(tx,ty) → head(hx,hy). Proportions are FIXED fractions of the
    // length L, so the pointer enlarges (never distorts) as the anchors move apart. Null for a degenerate one.
    public static PathF? BlockArrowPath(float tx, float ty, float hx, float hy)
    {
        float dx = hx - tx, dy = hy - ty;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 4f) return null;
        float ux = dx / len, uy = dy / len;      // unit tail→head
        float nx = -uy, ny = ux;                  // unit perpendicular
        float headLen = 0.40f * len, headHW = 0.25f * len, shaftHW = 0.12f * len;
        float bx = hx - ux * headLen, by = hy - uy * headLen;   // head base (shaft ↔ barb junction)
        var path = new PathF();
        path.MoveTo(tx + nx * shaftHW, ty + ny * shaftHW);      // shaft back, +side
        path.LineTo(bx + nx * shaftHW, by + ny * shaftHW);      // shaft front, +side
        path.LineTo(bx + nx * headHW,  by + ny * headHW);       // barb, +side
        path.LineTo(hx, hy);                                    // tip
        path.LineTo(bx - nx * headHW,  by - ny * headHW);       // barb, −side
        path.LineTo(bx - nx * shaftHW, by - ny * shaftHW);      // shaft front, −side
        path.LineTo(tx - nx * shaftHW, ty - ny * shaftHW);      // shaft back, −side
        path.Close();
        return path;
    }

    // Pull `tip` back toward `prev` by `dist` px (ends a freehand stroke at its head's base).
    public static PointF PullBack(PointF tip, PointF prev, float dist)
    {
        float dx = tip.X - prev.X, dy = tip.Y - prev.Y;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-4f) return tip;
        return new PointF(tip.X - dx / len * dist, tip.Y - dy / len * dist);
    }

    // Returns a human-friendly axis range and tick step that neatly covers [min, max].
    public static (double niceMin, double niceMax, double step) NiceRange(double min, double max, int maxTicks)
    {
        var range = NiceNum(max - min, round: false);
        var step = NiceNum(range / (maxTicks - 1), round: true);
        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;
        return (niceMin, niceMax, step);

        // Rounds x to a "nice" number (1, 2, 5, 10 …) — classic Wilkinson algorithm.
        static double NiceNum(double x, bool round)
        {
            var exp = Math.Floor(Math.Log10(x));
            var f = x / Math.Pow(10, exp);
            double nf;
            if (round)
            {
                if (f < 1.5) nf = 1;
                else if (f < 3) nf = 2;
                else if (f < 7) nf = 5;
                else nf = 10;
            }
            else
            {
                if (f <= 1) nf = 1;
                else if (f <= 2) nf = 2;
                else if (f <= 5) nf = 5;
                else nf = 10;
            }
            return nf * Math.Pow(10, exp);
        }
    }

    // Picks the time-step from a fixed candidate list that produces the closest number of ticks to targetTicks.
    public static TimeSpan ChooseTimeStep(DateTime from, DateTime to, int targetTicks)
    {
        var total = to - from;
        var candidates = new[]
        {
            TimeSpan.FromSeconds(1),  TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),  TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
            TimeSpan.FromHours(2),    TimeSpan.FromHours(4),
            TimeSpan.FromHours(6),    TimeSpan.FromHours(12),
            TimeSpan.FromDays(1),     TimeSpan.FromDays(7)
        };

        TimeSpan best = candidates[0];
        double bestDiff = double.MaxValue;
        foreach (var c in candidates)
        {
            var ticks = Math.Max(1, (int)Math.Round(total.TotalSeconds / c.TotalSeconds));
            var diff = Math.Abs(ticks - targetTicks);
            if (diff < bestDiff) { bestDiff = diff; best = c; }
        }
        return best;
    }

    // Snaps t to the nearest multiple of step, rounding forward or backward.
    public static DateTime AlignToStep(DateTime t, TimeSpan step, bool forward)
    {
        var ticks = step.Ticks;
        long k = t.Ticks / ticks;
        long aligned = forward
            ? ((t.Ticks % ticks) == 0 ? t.Ticks : (k + 1) * ticks)
            : k * ticks;
        return new DateTime(aligned, DateTimeKind.Utc);
    }

    // Human-friendly span for the measure label: coarsest two units that carry signal.
    public static string HumanizeSpan(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }

    // Price <-> normalized vertical fraction (0 = plot bottom, 1 = plot top), keyed on the scale
    // mode. Log maps equal RATIOS to equal pixels; Linear/Percent are plain linear. Bodies are the
    // drawable's old instance helpers verbatim, with the live ScaleMode read replaced by a parameter.
    public static double PriceToFrac(double price, double lo, double hi, PriceScaleMode mode)
    {
        if (mode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return b <= a ? 0.0 : (Math.Log(Math.Max(price, 1e-9)) - a) / (b - a);
        }
        return hi <= lo ? 0.0 : (price - lo) / (hi - lo);
    }

    public static double FracToPrice(double frac, double lo, double hi, PriceScaleMode mode)
    {
        if (mode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return Math.Exp(a + frac * (b - a));
        }
        return lo + frac * (hi - lo);
    }
}
