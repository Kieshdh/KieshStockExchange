using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Pointer hit-testing against the last paint's committed geometry. Pure read-side collaborator:
// everything arrives per call (the RenderFrame + the live data lists) or at construction (the
// drawable's tolerance consts) — no reference back to CandleChartDrawable, no theme, no canvas.
// Forward data->pixel transforms come from the frame's HIT-form helpers (subtract-in-double,
// cast last), so hit zones land on the exact pixels the render pass used. Y routes through
// PriceToFrac so the log scale is honoured just like the axes.
internal sealed class ChartHitTester
{
    // Injected copies of the drawable's consts, field-named identically so the moved hit bodies
    // stay verbatim (DrawHandleR/DrawHitTol pair with the drawn handle + line shapes; RightAxisW
    // extends order-line hits across the right gutter tag).
    private readonly float DrawHandleR;
    private readonly float DrawHitTol;
    private readonly float RightAxisW;

    // Plain-text metrics — kept in lockstep with DrawingRenderer's Text* consts so the clickable zone
    // matches the drawn glyphs. FontSize==0 (legacy/unset) uses TextDefaultFont.
    private const float TextMinW = 26f;
    private const float TextDefaultFont = 12f;
    private const float TextGlyphWFactor = 0.6f;

    public ChartHitTester(float drawHandleR, float drawHitTol, float rightAxisW)
    {
        DrawHandleR = drawHandleR;
        DrawHitTol = drawHitTol;
        RightAxisW = rightAxisW;
    }

    /// <summary>
    /// Returns the drawing hit by the pointer and which part (an endpoint, the body, or the ✕
    /// remove glyph). Searches topmost-first so the most recently added drawing wins overlaps.
    /// </summary>
    public (DrawingObject Drawing, DrawingHitPart Part)? HitDrawing(
        RenderFrame frame, IReadOnlyList<DrawingObject> drawings, PointF p)
    {
        if (drawings.Count == 0) return null;
        if (frame.Plot.Width <= 0 || frame.YMax <= frame.YMin) return null;

        for (int i = drawings.Count - 1; i >= 0; i--)
        {
            var d = drawings[i];
            if (d.Kind == DrawTool.HLine)
            {
                float y = frame.HitPriceToPixelY(d.P1);
                if (y < frame.Plot.Top || y > frame.Plot.Bottom) continue;
                if (p.X >= frame.Plot.Left && p.X <= frame.Plot.Right && Math.Abs(p.Y - y) <= DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Alert)
            {
                // Alert is a horizontal level at P1 — hit anywhere along the line across the plot (like HLine).
                float y = frame.HitPriceToPixelY(d.P1);
                if (y < frame.Plot.Top || y > frame.Plot.Bottom) continue;
                if (p.X >= frame.Plot.Left && p.X <= frame.Plot.Right && Math.Abs(p.Y - y) <= DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Crossline)
            {
                // Cross: hit if near the horizontal (at P1) OR the vertical (at T1).
                float cy = frame.HitPriceToPixelY(d.P1), cx = frame.TimeToPixelX(d.T1);
                bool onH = cy >= frame.Plot.Top && cy <= frame.Plot.Bottom
                    && p.X >= frame.Plot.Left && p.X <= frame.Plot.Right && Math.Abs(p.Y - cy) <= DrawHitTol;
                bool onV = cx >= frame.Plot.Left && cx <= frame.Plot.Right
                    && p.Y >= frame.Plot.Top && p.Y <= frame.Plot.Bottom && Math.Abs(p.X - cx) <= DrawHitTol;
                if (onH || onV) return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Text)
            {
                // Text label: hit anywhere inside the plain-text bounds at the anchor. The rect MUST mirror
                // the renderer's Text arm — left edge at the anchor X, vertically centred on the anchor Y,
                // width from the label length × the effective font size (lockstep with DrawingRenderer's Text* consts).
                if (string.IsNullOrEmpty(d.Text)) continue;
                float ax = frame.TimeToPixelX(d.T1), ay = frame.HitPriceToPixelY(d.P1);
                if (ax < frame.Plot.Left || ax > frame.Plot.Right || ay < frame.Plot.Top || ay > frame.Plot.Bottom) continue;
                float fontSize = d.Style.FontSize > 0 ? d.Style.FontSize : TextDefaultFont;
                float w = Math.Max(TextMinW, d.Text.Length * fontSize * TextGlyphWFactor);
                var r = new RectF(ax, ay - fontSize, w, fontSize * 2f);
                if (r.Contains(p)) return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Comment)
            {
                // Callout: hit inside the bubble rect above the anchor (mirror DrawingRenderer's Comment arm).
                if (string.IsNullOrEmpty(d.Text)) continue;
                float ax = frame.TimeToPixelX(d.T1), ay = frame.HitPriceToPixelY(d.P1);
                if (ax < frame.Plot.Left || ax > frame.Plot.Right || ay < frame.Plot.Top || ay > frame.Plot.Bottom) continue;
                float fontSize = d.Style.FontSize > 0 ? d.Style.FontSize : TextDefaultFont;
                const float padX = 6f, padY = 4f, tail = 7f;
                float bw = Math.Max(TextMinW, d.Text.Length * fontSize * TextGlyphWFactor) + padX * 2f;
                float bh = fontSize + padY * 2f;
                var r = new RectF(ax - bw / 2f, ay - tail - bh, bw, bh);
                if (r.Contains(p)) return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.HRay)
            {
                float x1 = frame.TimeToPixelX(d.T1), y = frame.HitPriceToPixelY(d.P1);
                if (ChartGeometry.Dist(p.X, p.Y, x1, y) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (p.X >= x1 - DrawHitTol && p.X <= frame.Plot.Right && Math.Abs(p.Y - y) <= DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.VLine)
            {
                float x = frame.TimeToPixelX(d.T1);
                if (x < frame.Plot.Left || x > frame.Plot.Right) continue;
                if (Math.Abs(p.X - x) <= DrawHitTol && p.Y >= frame.Plot.Top && p.Y <= frame.Plot.Bottom)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Polyline || d.Kind == DrawTool.Freehand)
            {
                var pts = d.Points;
                if (pts is null || pts.Count == 0) continue;
                float lastX = frame.TimeToPixelX(pts[0].T), lastY = frame.HitPriceToPixelY(pts[0].P);
                for (int k = 1; k < pts.Count; k++)
                {
                    float nx = frame.TimeToPixelX(pts[k].T), ny = frame.HitPriceToPixelY(pts[k].P);
                    if (ChartGeometry.PointSegDist(p.X, p.Y, lastX, lastY, nx, ny) <= DrawHitTol)
                        return (d, DrawingHitPart.Body);
                    lastX = nx; lastY = ny;
                }
            }
            else if (d.Kind == DrawTool.ExtendedLine)
            {
                float x1 = frame.TimeToPixelX(d.T1), y1 = frame.HitPriceToPixelY(d.P1);
                float x2 = frame.TimeToPixelX(d.T2), y2 = frame.HitPriceToPixelY(d.P2);
                if (ChartGeometry.Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (ChartGeometry.Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                var (ax, ay) = ChartGeometry.RayExit(x1, y1, x2 - x1, y2 - y1, frame.Plot);
                var (bx, by) = ChartGeometry.RayExit(x1, y1, x1 - x2, y1 - y2, frame.Plot);
                if (ChartGeometry.PointSegDist(p.X, p.Y, bx, by, ax, ay) <= DrawHitTol
                    && p.X >= frame.Plot.Left - 2 && p.X <= frame.Plot.Right + 2
                    && p.Y >= frame.Plot.Top - 2 && p.Y <= frame.Plot.Bottom + 2)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Rectangle || d.Kind == DrawTool.Ellipse)
            {
                float x1 = frame.TimeToPixelX(d.T1), y1 = frame.HitPriceToPixelY(d.P1);
                float x2 = frame.TimeToPixelX(d.T2), y2 = frame.HitPriceToPixelY(d.P2);
                if (ChartGeometry.Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (ChartGeometry.Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
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
                float x1 = frame.TimeToPixelX(d.T1), y1 = frame.HitPriceToPixelY(d.P1);
                float x2 = frame.TimeToPixelX(d.T2), y2 = frame.HitPriceToPixelY(d.P2);
                if (ChartGeometry.Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (ChartGeometry.Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                // Body = the block-arrow's oriented bounding box (anchors' bbox padded by the head half-width).
                float amin = Math.Min(x1, x2), amax = Math.Max(x1, x2);
                float bmin = Math.Min(y1, y2), bmax = Math.Max(y1, y2);
                float apad = 0.25f * ChartGeometry.Dist(x1, y1, x2, y2);
                if (p.X >= amin - apad && p.X <= amax + apad && p.Y >= bmin - apad && p.Y <= bmax + apad)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.FibRetracement)
            {
                // A Fib's visible shape is its horizontal levels inside the two-anchor box (NOT the Trend
                // diagonal). Anchors first so a handle-drag grabs the corners like Trend, then Body = near
                // ANY level line within the box's X-range.
                float x1 = frame.TimeToPixelX(d.T1), y1 = frame.HitPriceToPixelY(d.P1);
                float x2 = frame.TimeToPixelX(d.T2), y2 = frame.HitPriceToPixelY(d.P2);
                if (ChartGeometry.Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (ChartGeometry.Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                float left = Math.Min(x1, x2), right = Math.Max(x1, x2);
                if (p.X >= left - DrawHitTol && p.X <= right + DrawHitTol)
                    foreach (var lvl in FibonacciLevels.Levels(d.P1, d.P2))
                    {
                        float y = frame.HitPriceToPixelY(lvl.Price);
                        if (y < frame.Plot.Top || y > frame.Plot.Bottom) continue;
                        if (Math.Abs(p.Y - y) <= DrawHitTol) return (d, DrawingHitPart.Body);
                    }
            }
            else if (d.Kind == DrawTool.Position)
            {
                // Long/short box: the two draggable anchors are Entry(T1,P1) + Target(T2,P2) (like Trend);
                // Body = anywhere inside the box bounds — X between the anchors, Y spanning the entry/target/
                // stop legs. (The stop leg P3 has no anchor handle in v1.)
                float x1 = frame.TimeToPixelX(d.T1), y1 = frame.HitPriceToPixelY(d.P1);
                float x2 = frame.TimeToPixelX(d.T2), y2 = frame.HitPriceToPixelY(d.P2);
                if (ChartGeometry.Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (ChartGeometry.Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                float y3 = frame.HitPriceToPixelY(d.P3);
                float left = Math.Min(x1, x2), right = Math.Max(x1, x2);
                float top = Math.Min(y1, Math.Min(y2, y3)), bottom = Math.Max(y1, Math.Max(y2, y3));
                if (p.X >= left - DrawHitTol && p.X <= right + DrawHitTol
                    && p.Y >= top - DrawHitTol && p.Y <= bottom + DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else // Trend or Ray
            {
                float x1 = frame.TimeToPixelX(d.T1), y1 = frame.HitPriceToPixelY(d.P1);
                float x2 = frame.TimeToPixelX(d.T2), y2 = frame.HitPriceToPixelY(d.P2);
                if (ChartGeometry.Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (ChartGeometry.Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                // Ray body extends past anchor2 to the plot edge — hit-test the full drawn segment.
                float fx = x2, fy = y2;
                if (d.Kind == DrawTool.Ray) (fx, fy) = ChartGeometry.RayExit(x1, y1, x2 - x1, y2 - y1, frame.Plot);
                if (ChartGeometry.PointSegDist(p.X, p.Y, x1, y1, fx, fy) <= DrawHitTol
                    && p.X >= frame.Plot.Left - 2 && p.X <= frame.Plot.Right + 2
                    && p.Y >= frame.Plot.Top - 2 && p.Y <= frame.Plot.Bottom + 2)
                    return (d, DrawingHitPart.Body);
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the open-order line hit by the pointer (within 4 px of the line).
    /// Covers the full width from the plot left edge to the right-gutter tag.
    /// </summary>
    public OpenOrderLine? HitOpenOrderLine(
        RenderFrame frame, IReadOnlyList<OpenOrderLine> openOrderLines,
        int? draggingOrderId, decimal? draggingOrderPrice, PointF pInControl)
    {
        if (openOrderLines.Count == 0) return null;
        if (frame.Plot.Width <= 0 || frame.YMax <= frame.YMin) return null;

        for (int i = 0; i < openOrderLines.Count; i++)
        {
            var line = openOrderLines[i];
            // Mirror the draw-time price selection: when the user is mid-modify
            // we paint the line at DraggingOrderPrice, not line.Price. The hit
            // zone must follow or the visible line and the clickable line drift
            // apart — a second drag would miss because the cursor is over the
            // visual position but the test fires against the DB position.
            bool dragging = draggingOrderId == line.OrderId;
            decimal price = dragging && draggingOrderPrice is decimal dp ? dp : line.Price;
            float y = (float)(frame.Plot.Bottom
                              - ((double)price - frame.YMin) / (frame.YMax - frame.YMin)
                                * frame.Plot.Height);
            // Skip lines whose price is currently outside the visible Y range —
            // they aren't drawn, so they shouldn't be hit-testable either.
            if (y < frame.Plot.Top || y > frame.Plot.Bottom) continue;
            if (Math.Abs(pInControl.Y - y) > 4f) continue;
            if (pInControl.X < frame.Plot.Left
                || pInControl.X > frame.Plot.Right + RightAxisW) continue;
            return line;
        }
        return null;
    }

    /// <summary>
    /// Returns the index into the candle list whose bucket contains the X
    /// pixel, or null if the pointer falls into empty pre-history / future space.
    /// Accepts pointer positions inside the price pane or the volume sub-pane —
    /// both share the same time axis.
    /// </summary>
    public int? HitCandleIndex(RenderFrame frame, IReadOnlyList<Candle> candles, PointF pInControl)
    {
        if (candles.Count == 0) return null;
        if (pInControl.X < frame.Plot.Left || pInControl.X > frame.Plot.Right) return null;
        bool inPrice = pInControl.Y >= frame.Plot.Top && pInControl.Y <= frame.Plot.Bottom;
        bool inVol = frame.VolRect.Height > 0
                     && pInControl.Y >= frame.VolRect.Top
                     && pInControl.Y <= frame.VolRect.Bottom;
        if (!inPrice && !inVol) return null;

        var t = frame.PixelToTime(pInControl.X);

        // Binary search for the candle whose [OpenTime, CloseTime) contains t.
        int lo = 0, hi = candles.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var c = candles[mid];
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
    public bool IsInChartArea(RenderFrame frame, PointF pInControl)
    {
        if (frame.Plot.Contains(pInControl)) return true;
        return frame.VolRect.Height > 0 && frame.VolRect.Contains(pInControl);
    }

    /// <summary>
    /// True when the pointer is over the right-hand Y-axis gutter — the strip
    /// to the right of the price plot reserved for price labels. Wheel events
    /// here zoom the Y axis instead of the X axis.
    /// </summary>
    public bool IsInYAxisGutter(RenderFrame frame, PointF pInControl)
    {
        return pInControl.X > frame.Plot.Right
            && pInControl.X <= frame.Plot.Right + RightAxisW
            && pInControl.Y >= frame.Plot.Top
            && pInControl.Y <= frame.Plot.Bottom;
    }
}
