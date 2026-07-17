using System.Globalization;
using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class CandleChartDrawable
{
    // Forward data->pixel transforms now live on the last paint's RenderFrame (HIT form —
    // subtract-in-double, cast last), so hit-testing maps a drawing's data anchors back to the
    // exact pixels the render pass used. Y routes through PriceToFrac so the log scale is honoured
    // just like the axes.

    /// <summary>
    /// Returns the drawing hit by the pointer and which part (an endpoint, the body, or the ✕
    /// remove glyph). Searches topmost-first so the most recently added drawing wins overlaps.
    /// </summary>
    public (DrawingObject Drawing, DrawingHitPart Part)? HitDrawing(PointF p)
    {
        if (Drawings.Count == 0) return null;
        if (_frame.Plot.Width <= 0 || _frame.YMax <= _frame.YMin) return null;

        for (int i = Drawings.Count - 1; i >= 0; i--)
        {
            var d = Drawings[i];
            if (d.Kind == DrawTool.HLine)
            {
                float y = _frame.HitPriceToPixelY(d.P1);
                if (y < _frame.Plot.Top || y > _frame.Plot.Bottom) continue;
                if (p.X >= _frame.Plot.Left && p.X <= _frame.Plot.Right && Math.Abs(p.Y - y) <= DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.HRay)
            {
                float x1 = _frame.TimeToPixelX(d.T1), y = _frame.HitPriceToPixelY(d.P1);
                if (Dist(p.X, p.Y, x1, y) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (p.X >= x1 - DrawHitTol && p.X <= _frame.Plot.Right && Math.Abs(p.Y - y) <= DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.VLine)
            {
                float x = _frame.TimeToPixelX(d.T1);
                if (x < _frame.Plot.Left || x > _frame.Plot.Right) continue;
                if (Math.Abs(p.X - x) <= DrawHitTol && p.Y >= _frame.Plot.Top && p.Y <= _frame.Plot.Bottom)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Polyline || d.Kind == DrawTool.Freehand)
            {
                var pts = d.Points;
                if (pts is null || pts.Count == 0) continue;
                float lastX = _frame.TimeToPixelX(pts[0].T), lastY = _frame.HitPriceToPixelY(pts[0].P);
                for (int k = 1; k < pts.Count; k++)
                {
                    float nx = _frame.TimeToPixelX(pts[k].T), ny = _frame.HitPriceToPixelY(pts[k].P);
                    if (PointSegDist(p.X, p.Y, lastX, lastY, nx, ny) <= DrawHitTol)
                        return (d, DrawingHitPart.Body);
                    lastX = nx; lastY = ny;
                }
            }
            else if (d.Kind == DrawTool.ExtendedLine)
            {
                float x1 = _frame.TimeToPixelX(d.T1), y1 = _frame.HitPriceToPixelY(d.P1);
                float x2 = _frame.TimeToPixelX(d.T2), y2 = _frame.HitPriceToPixelY(d.P2);
                if (Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                var (ax, ay) = RayExit(x1, y1, x2 - x1, y2 - y1, _frame.Plot);
                var (bx, by) = RayExit(x1, y1, x1 - x2, y1 - y2, _frame.Plot);
                if (PointSegDist(p.X, p.Y, bx, by, ax, ay) <= DrawHitTol
                    && p.X >= _frame.Plot.Left - 2 && p.X <= _frame.Plot.Right + 2
                    && p.Y >= _frame.Plot.Top - 2 && p.Y <= _frame.Plot.Bottom + 2)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Rectangle || d.Kind == DrawTool.Ellipse)
            {
                float x1 = _frame.TimeToPixelX(d.T1), y1 = _frame.HitPriceToPixelY(d.P1);
                float x2 = _frame.TimeToPixelX(d.T2), y2 = _frame.HitPriceToPixelY(d.P2);
                if (Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                var rect = new RectF(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
                // Body = on the border (within tolerance) or anywhere inside a filled shape.
                bool onBorder =
                    p.X >= rect.Left - DrawHitTol && p.X <= rect.Right + DrawHitTol &&
                    p.Y >= rect.Top - DrawHitTol && p.Y <= rect.Bottom + DrawHitTol &&
                    (Math.Abs(p.X - rect.Left) <= DrawHitTol || Math.Abs(p.X - rect.Right) <= DrawHitTol ||
                     Math.Abs(p.Y - rect.Top) <= DrawHitTol || Math.Abs(p.Y - rect.Bottom) <= DrawHitTol);
                bool inside = d.Style.Fill is not null && rect.Contains(p);
                if (onBorder || inside) return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Arrow)
            {
                float x1 = _frame.TimeToPixelX(d.T1), y1 = _frame.HitPriceToPixelY(d.P1);
                float x2 = _frame.TimeToPixelX(d.T2), y2 = _frame.HitPriceToPixelY(d.P2);
                if (Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                // Body = the block-arrow's oriented bounding box (anchors' bbox padded by the head half-width).
                float amin = Math.Min(x1, x2), amax = Math.Max(x1, x2);
                float bmin = Math.Min(y1, y2), bmax = Math.Max(y1, y2);
                float apad = 0.25f * Dist(x1, y1, x2, y2);
                if (p.X >= amin - apad && p.X <= amax + apad && p.Y >= bmin - apad && p.Y <= bmax + apad)
                    return (d, DrawingHitPart.Body);
            }
            else // Trend or Ray
            {
                float x1 = _frame.TimeToPixelX(d.T1), y1 = _frame.HitPriceToPixelY(d.P1);
                float x2 = _frame.TimeToPixelX(d.T2), y2 = _frame.HitPriceToPixelY(d.P2);
                if (Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                // Ray body extends past anchor2 to the plot edge — hit-test the full drawn segment.
                float fx = x2, fy = y2;
                if (d.Kind == DrawTool.Ray) (fx, fy) = RayExit(x1, y1, x2 - x1, y2 - y1, _frame.Plot);
                if (PointSegDist(p.X, p.Y, x1, y1, fx, fy) <= DrawHitTol
                    && p.X >= _frame.Plot.Left - 2 && p.X <= _frame.Plot.Right + 2
                    && p.Y >= _frame.Plot.Top - 2 && p.Y <= _frame.Plot.Bottom + 2)
                    return (d, DrawingHitPart.Body);
            }
        }
        return null;
    }

    private static float Dist(float ax, float ay, float bx, float by)
        => (float)Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    // Shortest distance from point (px,py) to the segment (ax,ay)-(bx,by).
    private static float PointSegDist(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        if (len2 <= 1e-6f) return Dist(px, py, ax, ay);
        float t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0f, 1f);
        return Dist(px, py, ax + t * dx, ay + t * dy);
    }

    /// <summary>
    /// Returns the open-order line hit by the pointer (within 4 px of the line).
    /// Covers the full width from the plot left edge to the right-gutter tag.
    /// </summary>
    public OpenOrderLine? HitOpenOrderLine(PointF pInControl)
    {
        if (OpenOrderLines.Count == 0) return null;
        if (_frame.Plot.Width <= 0 || _frame.YMax <= _frame.YMin) return null;

        for (int i = 0; i < OpenOrderLines.Count; i++)
        {
            var line = OpenOrderLines[i];
            // Mirror the draw-time price selection: when the user is mid-modify
            // we paint the line at DraggingOrderPrice, not line.Price. The hit
            // zone must follow or the visible line and the clickable line drift
            // apart — a second drag would miss because the cursor is over the
            // visual position but the test fires against the DB position.
            bool dragging = DraggingOrderId == line.OrderId;
            decimal price = dragging && DraggingOrderPrice is decimal dp ? dp : line.Price;
            float y = (float)(_frame.Plot.Bottom
                              - ((double)price - _frame.YMin) / (_frame.YMax - _frame.YMin)
                                * _frame.Plot.Height);
            // Skip lines whose price is currently outside the visible Y range —
            // they aren't drawn, so they shouldn't be hit-testable either.
            if (y < _frame.Plot.Top || y > _frame.Plot.Bottom) continue;
            if (Math.Abs(pInControl.Y - y) > 4f) continue;
            if (pInControl.X < _frame.Plot.Left
                || pInControl.X > _frame.Plot.Right + RightAxisW) continue;
            return line;
        }
        return null;
    }

    #region Hit-testing (public — used by ChartView pointer handlers)
    /// <summary>
    /// Maps a Y pixel inside the plot back to a price using the cached Y-range
    /// from the most recent paint. Returns null if no successful paint has been
    /// performed yet.
    /// </summary>
    public decimal? PixelToPrice(float yInControl) => _frame.PixelToPrice(yInControl);

    /// <summary>
    /// Maps an X pixel inside the plot back to a UTC time using the cached time
    /// range from the most recent paint.
    /// </summary>
    public DateTime PixelToTime(float xInControl) => _frame.PixelToTime(xInControl);

    /// <summary>
    /// Returns the index into <see cref="Candles"/> whose bucket contains the X
    /// pixel, or null if the pointer falls into empty pre-history / future space.
    /// Accepts pointer positions inside the price pane or the volume sub-pane —
    /// both share the same time axis.
    /// </summary>
    public int? HitCandleIndex(PointF pInControl)
    {
        if (Candles.Count == 0) return null;
        if (pInControl.X < _frame.Plot.Left || pInControl.X > _frame.Plot.Right) return null;
        bool inPrice = pInControl.Y >= _frame.Plot.Top && pInControl.Y <= _frame.Plot.Bottom;
        bool inVol = _frame.VolRect.Height > 0
                     && pInControl.Y >= _frame.VolRect.Top
                     && pInControl.Y <= _frame.VolRect.Bottom;
        if (!inPrice && !inVol) return null;

        var t = PixelToTime(pInControl.X);

        // Binary search for the candle whose [OpenTime, CloseTime) contains t.
        int lo = 0, hi = Candles.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var c = Candles[mid];
            if (t < c.OpenTime) hi = mid - 1;
            else if (t >= c.CloseTime) lo = mid + 1;
            else return mid;
        }
        return null;
    }

    /// <summary>
    /// True when the control-space pointer falls inside the price area or the
    /// volume sub-pane. Used by ChartView to decide when to hide the crosshair.
    /// </summary>
    public bool IsInChartArea(PointF pInControl)
    {
        if (_frame.Plot.Contains(pInControl)) return true;
        return _frame.VolRect.Height > 0 && _frame.VolRect.Contains(pInControl);
    }

    /// <summary>
    /// True when the pointer is over the right-hand Y-axis gutter — the strip
    /// to the right of the price plot reserved for price labels. Wheel events
    /// here zoom the Y axis instead of the X axis.
    /// </summary>
    public bool IsInYAxisGutter(PointF pInControl)
    {
        return pInControl.X > _frame.Plot.Right
            && pInControl.X <= _frame.Plot.Right + RightAxisW
            && pInControl.Y >= _frame.Plot.Top
            && pInControl.Y <= _frame.Plot.Bottom;
    }
    #endregion
}
