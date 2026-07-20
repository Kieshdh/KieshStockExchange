using System.Globalization;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// User-drawn shapes (every DrawTool), the in-progress polyline/freehand preview, and their
// labels/tags/handles. Stateless renderer collaborator: geometry and transforms arrive in the
// RenderFrame (the ExtendedLine/Rect/Ellipse/Arrow verticals route through its ScaleY seam),
// colours/fonts in the ChartTheme, and the live drawing state per call — no reference back to
// CandleChartDrawable. Gutter sizes + the handle radius are the spine's consts, injected once
// at construction (the ChartHitTester pattern; DrawHandleR stays spine-owned so the drawn handle
// and its clickable zone keep one source of truth).
internal sealed class DrawingRenderer
{
    private readonly float DrawHandleR;
    private readonly float RightAxisW;
    private readonly float BottomAxisH;

    public DrawingRenderer(float drawHandleR, float rightAxisW, float bottomAxisH)
    {
        DrawHandleR = drawHandleR;
        RightAxisW = rightAxisW;
        BottomAxisH = bottomAxisH;
    }

    // Fixed, always-readable pill background for the axis TAGS (price gutter + VLine time). Using the
    // drawing's own colour made light/yellow labels unreadable; a standard dark slate + white text reads
    // on any theme. The line's colour is kept as a thin border so the tag still identifies its drawing.
    private static readonly Color LabelPillBg = Color.FromArgb("#2A2E39");

    // Text-label pill metrics — width sizes to the label length. MUST match ChartHitTester's Text arm so
    // the clickable zone tracks the drawn pill exactly (there's no shared home across the two collaborators).
    private const float TextPillCharW = 7f;
    private const float TextPillMinW = 26f;
    private const float TextPillH = 16f;

    /// <summary>
    /// Draw the user's horizontal lines + trendlines. HLine spans the plot at its price with a
    /// right-gutter price tag; Trend is a segment with a draggable handle at each end. Both carry a
    /// ✕ remove glyph. Anchored in data space via the shared X/Y transforms, so they hold position
    /// through pan/zoom. Off-screen shapes are clipped by the same range checks the candles use.
    /// </summary>
    public void DrawDrawings(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<DrawingObject> drawings,
        Guid? draggingDrawingId, Guid? selectedDrawingId, IReadOnlyCollection<Guid>? selectedDrawingIds,
        IReadOnlyList<DrawPoint>? buildingPolyline, DrawPoint? buildingPolylineCursor,
        bool buildingIsFreehand, DrawStyle buildingStyle)
    {
        // Keep painting while a polyline is mid-build even with no committed drawings, else the
        // in-progress preview (drawn below) never shows until the first shape is committed.
        if (drawings.Count == 0 && buildingPolyline is null) return;
        var plot = f.Plot;
        var cur = f.Currency;
        float X(DateTime utc) => f.MapX(utc);
        float Y(double price) => f.MapY(price);
        canvas.SaveState();
        for (int i = 0; i < drawings.Count; i++)
        {
            var d = drawings[i];
            bool active = draggingDrawingId == d.Id;
            bool selected = selectedDrawingId == d.Id || (selectedDrawingIds?.Contains(d.Id) ?? false);
            // Per-drawing style (colour/thickness/dash); fall back to the theme colour when a
            // legacy drawing carries no colour. A selected/active line paints a touch thicker.
            var color = d.Style.Color ?? t.DrawingColor;
            float thickness = d.Style.Thickness > 0f ? d.Style.Thickness : 1.5f;
            // A selected/active line paints a touch thicker. DrawStraightSegment sets its own stroke, so
            // this pre-set only serves the polyline branch (which draws its own multi-segment line).
            float stroke = (active || selected) ? thickness + 1f : thickness;
            var dashPattern = ChartGeometry.DashPattern(d.Style.Dash);
            canvas.StrokeColor = color;
            canvas.StrokeSize = stroke;
            canvas.StrokeDashPattern = dashPattern;

            if (d.Kind == DrawTool.HLine)
            {
                float y = Y((double)d.P1);
                if (y < plot.Top || y > plot.Bottom) { canvas.StrokeDashPattern = null; continue; }
                // Horizontal lines never carry a head (they don't "stop") — force None even if a shared
                // default pen carries an ending. Matches the pen panel hiding End/Head for these tools.
                StylePreviewDrawable.DrawStraightSegment(canvas, plot.Left, y, plot.Right, y,
                    LineEnding.None, color, stroke, dashPattern, d.Style.Head, ChartGeometry.EndSize(thickness));

                // Right-gutter price tag in the line's colour, matching the order-line convention.
                DrawGutterPriceTag(canvas, t, plot, y, d.P1, color, cur);
                // Selection: grab-handles at the ends so it reads as "editable" like a trendline.
                if (selected)
                {
                    DrawHandle(canvas, t, plot.Left + 1f, y, color);
                    DrawHandle(canvas, t, plot.Right - 1f, y, color);
                }
            }
            else if (d.Kind == DrawTool.HRay)
            {
                // Horizontal ray: from the click time rightward to the plot edge at price P1.
                float y = Y((double)d.P1);
                if (y < plot.Top || y > plot.Bottom) { canvas.StrokeDashPattern = null; continue; }
                float x1 = X(d.T1);
                // Origin = click; terminal = plot right edge. No head (horizontal ray doesn't "stop").
                StylePreviewDrawable.DrawStraightSegment(canvas, x1, y, plot.Right, y,
                    LineEnding.None, color, stroke, dashPattern, d.Style.Head, ChartGeometry.EndSize(thickness));
                DrawGutterPriceTag(canvas, t, plot, y, d.P1, color, cur);
                if (selected) DrawHandle(canvas, t, x1, y, color);
            }
            else if (d.Kind == DrawTool.VLine)
            {
                // Vertical time line: spans the full plot height at the anchor's time. No ending/head.
                float x = X(d.T1);
                if (x < plot.Left || x > plot.Right) { canvas.StrokeDashPattern = null; continue; }
                canvas.StrokeColor = color;
                canvas.StrokeSize = stroke;
                canvas.StrokeDashPattern = dashPattern;
                canvas.DrawLine(x, plot.Top, x, plot.Bottom);
                canvas.StrokeDashPattern = null;
                // Bottom-axis time tag (mirrors the HLine/HRay price gutter tag).
                DrawVLineTimeTag(canvas, t, plot, x, d.T1, color);
                if (selected)
                {
                    DrawHandle(canvas, t, x, plot.Top + 1f, color);
                    DrawHandle(canvas, t, x, plot.Bottom - 1f, color);
                }
            }
            else if (d.Kind == DrawTool.Polyline)
            {
                var pts = d.Points;
                if (pts is null || pts.Count == 0) { canvas.StrokeDashPattern = null; continue; }
                int n = pts.Count;
                // Endings apply only to the first + last vertex. Terminate the first/last segment at the
                // head base (like the straight kinds) so the hollow head reads clean; clamp so a 2-point
                // polyline (one segment shared by both heads) keeps a visible middle gap.
                bool polyHeadStart = d.Style.Ending is LineEnding.Start or LineEnding.BothOut;
                bool polyHeadEnd = d.Style.Ending is LineEnding.End or LineEnding.BothOut or LineEnding.BothForward;
                float polyEff = ChartGeometry.EndSize(thickness), startCut = 0f, endCut = 0f;
                if (n >= 2)
                {
                    float seg0 = ChartGeometry.Dist(X(pts[0].T), Y((double)pts[0].P), X(pts[1].T), Y((double)pts[1].P));
                    float segN = ChartGeometry.Dist(X(pts[n - 2].T), Y((double)pts[n - 2].P), X(pts[n - 1].T), Y((double)pts[n - 1].P));
                    float lim = float.MaxValue;
                    if (polyHeadStart) lim = Math.Min(lim, seg0 * (n == 2 && polyHeadEnd ? 0.30f : 0.6f));
                    if (polyHeadEnd) lim = Math.Min(lim, segN * (n == 2 && polyHeadStart ? 0.30f : 0.6f));
                    polyEff = Math.Min(ChartGeometry.EndSize(thickness), lim);
                    // Open head = hollow barb: line runs to the tip (no base-cut), matching the straight kinds.
                    bool polyCut = d.Style.Head != ArrowHeadStyle.Open;
                    startCut = polyHeadStart && polyCut ? polyEff : 0f;
                    endCut = polyHeadEnd && polyCut ? polyEff : 0f;
                }
                for (int k = 1; k < n; k++)
                {
                    float axk = X(pts[k - 1].T), ayk = Y((double)pts[k - 1].P);
                    float bxk = X(pts[k].T), byk = Y((double)pts[k].P);
                    if (k == 1 && startCut > 0f)
                    {
                        float ddx = bxk - axk, ddy = byk - ayk, dl = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                        if (dl > 1e-4f) { axk += ddx / dl * startCut; ayk += ddy / dl * startCut; }
                    }
                    if (k == n - 1 && endCut > 0f)
                    {
                        float ddx = bxk - axk, ddy = byk - ayk, dl = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                        if (dl > 1e-4f) { bxk -= ddx / dl * endCut; byk -= ddy / dl * endCut; }
                    }
                    canvas.DrawLine(axk, ayk, bxk, byk);
                }
                canvas.StrokeDashPattern = null;
                // Endings: the start head follows the first→second segment, the end head the last one.
                if (n >= 2)
                {
                    float fx0 = X(pts[0].T), fy0 = Y((double)pts[0].P);
                    float sx = X(pts[1].T), sy = Y((double)pts[1].P);
                    float slx = X(pts[^2].T), sly = Y((double)pts[^2].P);
                    float lx = X(pts[^1].T), ly = Y((double)pts[^1].P);
                    StylePreviewDrawable.DrawEndings(canvas, fx0, fy0, sx - fx0, sy - fy0,
                        lx, ly, lx - slx, ly - sly, d.Style.Ending, color, polyEff, d.Style.Head, thickness);
                }
                if (selected)
                    for (int k = 0; k < pts.Count; k++)
                        DrawHandle(canvas, t, X(pts[k].T), Y((double)pts[k].P), color);
            }
            else if (d.Kind == DrawTool.ExtendedLine)
            {
                // Infinite line through both anchors, extended to BOTH plot edges. Vertical mapping
                // routes through the scale seam (identical to the local Y under RegularScaleTransform).
                float x1 = X(d.T1), y1 = f.ScaleY(d.P1);
                float x2 = X(d.T2), y2 = f.ScaleY(d.P2);
                var (ax, ay) = ChartGeometry.RayExit(x1, y1, x2 - x1, y2 - y1, plot);   // forward edge
                var (bx, by) = ChartGeometry.RayExit(x1, y1, x1 - x2, y1 - y2, plot);   // backward edge
                StylePreviewDrawable.DrawStraightSegment(canvas, bx, by, ax, ay,
                    LineEnding.None, color, stroke, dashPattern, d.Style.Head, ChartGeometry.EndSize(thickness));
                if (selected)
                {
                    DrawHandle(canvas, t, x1, y1, color);
                    DrawHandle(canvas, t, x2, y2, color);
                }
            }
            else if (d.Kind == DrawTool.Rectangle || d.Kind == DrawTool.Ellipse)
            {
                // Two-corner shape: optional translucent fill (Fill + FillOpacity) then a border stroke.
                // Corners' vertical mapping routes through the scale seam.
                float x1 = X(d.T1), y1 = f.ScaleY(d.P1);
                float x2 = X(d.T2), y2 = f.ScaleY(d.P2);
                var rect = new RectF(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

                if (d.Style.Fill is Color fill)
                {
                    canvas.FillColor = fill.WithAlpha(Math.Clamp(d.Style.FillOpacity, 0f, 1f));
                    if (d.Kind == DrawTool.Rectangle) canvas.FillRectangle(rect); else canvas.FillEllipse(rect);
                }
                canvas.StrokeColor = color;
                canvas.StrokeSize = stroke;
                canvas.StrokeDashPattern = dashPattern;
                if (d.Kind == DrawTool.Rectangle) canvas.DrawRectangle(rect); else canvas.DrawEllipse(rect);

                if (selected)
                {
                    DrawHandle(canvas, t, x1, y1, color);
                    DrawHandle(canvas, t, x2, y2, color);
                }
            }
            else if (d.Kind == DrawTool.Freehand)
            {
                // Free-drawn brush: a captured point path rounded by the pen's Smoothing, in the pen's width
                // + dash. An optional ending head hangs on the terminal point(s); the smooth stroke stops at
                // the head BASE (like the straight kinds) so the line never shows through a hollow/filled
                // head. No selection handles — a freehand reads as a clean stroke (owner preference).
                var fpts = d.Points;
                if (fpts is null || fpts.Count < 2) { canvas.StrokeDashPattern = null; continue; }
                var fscr = new PointF[fpts.Count];
                for (int k = 0; k < fpts.Count; k++) fscr[k] = new PointF(X(fpts[k].T), Y((double)fpts[k].P));

                var fEnding = d.Style.Ending;
                bool fHeadStart = fEnding is LineEnding.Start or LineEnding.BothOut;
                bool fHeadEnd = fEnding is LineEnding.End or LineEnding.BothOut or LineEnding.BothForward;
                bool fCut = d.Style.Head != ArrowHeadStyle.Open;   // Open barb runs the line to the tip
                float fEff = ChartGeometry.EndSize(thickness);

                var fbody = fscr;
                if (fEnding != LineEnding.None && fCut && (fHeadStart || fHeadEnd))
                {
                    // Trim past the head base by the round-cap radius + a hair, so the rounded stroke end
                    // tucks fully under the head (no line poking through the tip).
                    float fTrim = fEff + stroke * 0.5f + 1.5f;
                    fbody = (PointF[])fscr.Clone();
                    if (fHeadEnd) fbody[^1] = ChartGeometry.PullBack(fscr[^1], fscr[^2], fTrim);
                    if (fHeadStart) fbody[0] = ChartGeometry.PullBack(fscr[0], fscr[1], fTrim);
                }

                canvas.StrokeColor = color;
                canvas.StrokeSize = stroke;
                canvas.StrokeDashPattern = dashPattern;
                DrawFreehandPath(canvas, fbody, d.Smoothing);
                canvas.StrokeDashPattern = null;

                if (fEnding != LineEnding.None)
                    StylePreviewDrawable.DrawEndings(canvas,
                        fscr[0].X, fscr[0].Y, fscr[1].X - fscr[0].X, fscr[1].Y - fscr[0].Y,
                        fscr[^1].X, fscr[^1].Y, fscr[^1].X - fscr[^2].X, fscr[^1].Y - fscr[^2].Y,
                        fEnding, color, fEff, d.Style.Head, thickness);
            }
            else if (d.Kind == DrawTool.Arrow)
            {
                // Filled BLOCK ARROW: anchor1 = tail, anchor2 = head. Fixed aspect (proportional to the
                // length), so moving the anchors apart just ENLARGES the same pointer. Fill then outline —
                // the same fill/opacity + stroke treatment as Rectangle/Ellipse.
                float x1 = X(d.T1), y1 = f.ScaleY(d.P1);
                float x2 = X(d.T2), y2 = f.ScaleY(d.P2);
                var arrow = ChartGeometry.BlockArrowPath(x1, y1, x2, y2);
                if (arrow is not null)
                {
                    if (d.Style.Fill is Color afill)
                    {
                        canvas.FillColor = afill.WithAlpha(Math.Clamp(d.Style.FillOpacity, 0f, 1f));
                        canvas.FillPath(arrow);
                    }
                    canvas.StrokeColor = color;
                    canvas.StrokeSize = stroke;
                    canvas.StrokeDashPattern = dashPattern;
                    canvas.DrawPath(arrow);
                }
                if (selected)
                {
                    DrawHandle(canvas, t, x1, y1, color);   // tail
                    DrawHandle(canvas, t, x2, y2, color);   // head
                }
            }
            else if (d.Kind == DrawTool.Alert)
            {
                // Price alert: a dashed level at P1 with a bell at the plot's left edge + a right-gutter
                // price tag (clipped off-plot like HLine). Once the live price has reached/crossed the
                // level it reads "fired" — solid + thicker line and a warning-tinted bell — so an armed
                // vs triggered alert is legible at a glance (a proper cross-latch is deferred).
                float y = Y((double)d.P1);
                if (y < plot.Top || y > plot.Bottom) { canvas.StrokeDashPattern = null; continue; }
                bool triggered = f.CurrentPrice is decimal acp && acp >= d.P1;
                var alertColor = triggered ? t.Bear : color;
                StylePreviewDrawable.DrawStraightSegment(canvas, plot.Left, y, plot.Right, y,
                    LineEnding.None, alertColor, triggered ? stroke + 1f : stroke,
                    triggered ? null : dashPattern, d.Style.Head, ChartGeometry.EndSize(thickness));

                // Bell glyph (🔔) at the left edge, in the line's colour (warning tint when fired).
                canvas.FontColor = alertColor;
                canvas.FontSize = t.PriceTagFont + 3f;
                canvas.DrawString("\U0001F514", new RectF(plot.Left + 1f, y - 9f, 18f, 18f),
                    HorizontalAlignment.Left, VerticalAlignment.Center);

                DrawGutterPriceTag(canvas, t, plot, y, d.P1, alertColor, cur);
                if (selected) DrawHandle(canvas, t, plot.Left + 1f, y, alertColor);
            }
            else if (d.Kind == DrawTool.Text)
            {
                // Anchored label: a readable pill (dark slate + white text + thin colour border) with its
                // LEFT edge at the (T1,P1) anchor, vertically centred. Reuses the DrawEndpointPriceTag pill
                // mechanics. Blank text draws nothing; an off-plot anchor is clipped like the level kinds.
                // Pill metrics MUST mirror ChartHitTester's Text arm so the clickable zone tracks the paint.
                if (string.IsNullOrEmpty(d.Text)) { canvas.StrokeDashPattern = null; continue; }
                float ax = X(d.T1), ay = Y((double)d.P1);
                if (ax < plot.Left || ax > plot.Right || ay < plot.Top || ay > plot.Bottom)
                { canvas.StrokeDashPattern = null; continue; }
                float w = Math.Max(TextPillMinW, d.Text.Length * TextPillCharW);
                var r = new RectF(ax, ay - TextPillH / 2f, w, TextPillH);
                canvas.FillColor = LabelPillBg;
                canvas.FillRectangle(r);
                canvas.StrokeColor = color; canvas.StrokeSize = 1.5f;
                canvas.StrokeDashPattern = null;   // the pill border is never dashed (pen dash is for the line kinds)
                canvas.DrawRectangle(r);
                canvas.FontColor = Colors.White;
                canvas.FontSize = t.PriceTagFont + 1f;   // a touch larger than the axis tags so labels read
                canvas.DrawString(d.Text, new RectF(r.X + 3, r.Y, r.Width - 6, r.Height),
                    HorizontalAlignment.Left, VerticalAlignment.Center);
                if (selected) DrawHandle(canvas, t, ax, ay, color);
            }
            else // Trend or Ray (both a two-anchor segment; Ray extends past anchor2 to the plot edge)
            {
                float x1 = X(d.T1), y1 = Y((double)d.P1);
                float x2 = X(d.T2), y2 = Y((double)d.P2);
                float farX = x2, farY = y2;
                if (d.Kind == DrawTool.Ray)
                    (farX, farY) = ChartGeometry.RayExit(x1, y1, x2 - x1, y2 - y1, plot);
                // Origin = anchor1; terminal = anchor2 (Trend) / ray-exit (Ray). Forward = origin→terminal.
                // Ray runs to infinity past anchor2, so it never carries an ending head (matches the pen
                // panel hiding End/Head for it, and forcing None so a shared default-pen ending can't leak).
                var trendEnding = d.Kind == DrawTool.Ray ? LineEnding.None : d.Style.Ending;
                StylePreviewDrawable.DrawStraightSegment(canvas, x1, y1, farX, farY,
                    trendEnding, color, stroke, dashPattern, d.Style.Head, ChartGeometry.EndSize(thickness));
                if (selected)
                {
                    DrawHandle(canvas, t, x1, y1, color);
                    DrawHandle(canvas, t, x2, y2, color);
                }
                // Trendline labels (always-on v1): endpoint prices + a midpoint change/% / bar-count
                // tag coloured by direction (TradingView convention).
                DrawTrendLabels(canvas, f, t, d, x1, y1, x2, y2, color, cur);
            }
            canvas.StrokeDashPattern = null;
        }

        DrawBuildingPolyline(canvas, f, t, buildingPolyline, buildingPolylineCursor, buildingIsFreehand, buildingStyle);
        canvas.RestoreState();
    }

    // Live preview of the polyline being built: the dropped vertices connected in order, plus a
    // rubber-band segment from the last vertex to the current cursor point. Drawn in the CURRENT pen
    // style (colour/width/dash + ending head) so the in-progress arrow reads exactly as it will commit.
    private void DrawBuildingPolyline(ICanvas canvas, RenderFrame f, ChartTheme theme,
        IReadOnlyList<DrawPoint>? buildingPolyline, DrawPoint? buildingPolylineCursor,
        bool buildingIsFreehand, DrawStyle buildingStyle)
    {
        var pts = buildingPolyline;
        if (pts is null || pts.Count == 0) return;
        float X(DateTime utc) => f.MapX(utc);
        float Y(double price) => f.MapY(price);
        var style = buildingStyle;
        var color = style.Color ?? DrawStyle.Default.Color;
        float thickness = style.Thickness > 0f ? style.Thickness : DrawStyle.Default.Thickness;

        // Screen-space points: the dropped vertices + (while hovering) the rubber-band cursor point.
        var scr = new List<(float x, float y)>(pts.Count + 1);
        for (int k = 0; k < pts.Count; k++) scr.Add((X(pts[k].T), Y((double)pts[k].P)));
        if (buildingPolylineCursor is DrawPoint c) scr.Add((X(c.T), Y((double)c.P)));

        canvas.StrokeColor = color;
        canvas.StrokeSize = thickness;
        canvas.StrokeDashPattern = ChartGeometry.DashPattern(style.Dash);
        if (buildingIsFreehand)
        {
            // Preview the SAME B-spline the committed freehand uses (smoothing=1), so the live stroke
            // rounds toward the captured points exactly as it will look once committed.
            var braw = new PointF[pts.Count];
            for (int k = 0; k < pts.Count; k++) braw[k] = new PointF(X(pts[k].T), Y((double)pts[k].P));
            DrawFreehandPath(canvas, braw, 1f);
        }
        else
        {
            for (int k = 1; k < scr.Count; k++)
                canvas.DrawLine(scr[k - 1].x, scr[k - 1].y, scr[k].x, scr[k].y);
        }
        canvas.StrokeDashPattern = null;

        // Ending head(s) — polyline only (freehand has no ending). Shown on the live segment ends so the
        // arrowhead reads WHILE drawing. Start head follows vertex0→vertex1; end follows the last two.
        if (!buildingIsFreehand && scr.Count >= 2 && style.Ending != LineEnding.None)
        {
            var (sx, sy) = scr[0]; var (s2x, s2y) = scr[1];
            var (lx, ly) = scr[^1]; var (l2x, l2y) = scr[^2];
            StylePreviewDrawable.DrawEndings(canvas, sx, sy, s2x - sx, s2y - sy,
                lx, ly, lx - l2x, ly - l2y, style.Ending, color, ChartGeometry.EndSize(thickness), style.Head, thickness);
        }

        // Vertex dots — polyline placement feedback only; a freehand previews as a bare smooth stroke.
        if (!buildingIsFreehand)
            for (int k = 0; k < pts.Count; k++)
                DrawHandle(canvas, theme, X(pts[k].T), Y((double)pts[k].P), color);
    }

    // Right-gutter price pill (shared by HLine + the trend endpoint tags).
    private void DrawGutterPriceTag(ICanvas canvas, ChartTheme theme, RectF plot, float y, decimal price, Color color, CurrencyType cur)
    {
        var tagRect = new RectF(plot.Right + 1, y - 8, RightAxisW - 2, 16);
        // Readable standard pill (dark slate + white text); the line's colour is a thin left border only.
        canvas.FillColor = LabelPillBg;
        canvas.FillRectangle(tagRect);
        canvas.StrokeColor = color; canvas.StrokeSize = 2f;
        canvas.DrawLine(tagRect.Left, tagRect.Top, tagRect.Left, tagRect.Bottom);
        canvas.FontColor = Colors.White;
        canvas.FontSize = theme.PriceTagFont;
        canvas.DrawString(CurrencyHelper.Format(price, cur),
            new RectF(tagRect.X + 4, tagRect.Y, tagRect.Width - 7, tagRect.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    // Bottom-axis time pill for a vertical line — the VLine analog of the HLine price gutter tag.
    private void DrawVLineTimeTag(ICanvas canvas, ChartTheme theme, RectF plot, float x, DateTime t, Color color)
    {
        string text = t.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
        float w = Math.Max(74f, text.Length * 6.3f);
        float lx = Math.Clamp(x - w / 2f, plot.Left, Math.Max(plot.Left, plot.Right - w));
        var r = new RectF(lx, plot.Bottom + 2, w, BottomAxisH - 2);
        // Readable standard pill (dark slate + white text) + a thin top border in the line's colour.
        canvas.FillColor = LabelPillBg;
        canvas.FillRectangle(r);
        canvas.StrokeColor = color; canvas.StrokeSize = 2f;
        canvas.DrawLine(r.Left, r.Top, r.Right, r.Top);
        canvas.FontColor = Colors.White;
        canvas.FontSize = theme.PriceTagFont;
        canvas.DrawString(text, new RectF(r.X + 3, r.Y, r.Width - 6, r.Height),
            HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    // A small price pill anchored beside a trendline endpoint (flips to the inside edge so it
    // doesn't spill off the plot). Painted in the line's colour with white text.
    private void DrawEndpointPriceTag(ICanvas canvas, ChartTheme theme, RectF plot, float x, float y, decimal price, Color color, CurrencyType cur, bool toLeft)
    {
        string text = CurrencyHelper.Format(price, cur);
        float w = Math.Max(40f, text.Length * 6.5f);
        float lx = toLeft ? x - w - 6f : x + 6f;
        lx = Math.Clamp(lx, plot.Left, Math.Max(plot.Left, plot.Right - w));
        float ly = Math.Clamp(y - 8f, plot.Top, Math.Max(plot.Top, plot.Bottom - 16f));
        var r = new RectF(lx, ly, w, 16f);
        // Readable standard pill + thin line-colour border (was line-colour fill = unreadable on light pens).
        canvas.FillColor = LabelPillBg;
        canvas.FillRectangle(r);
        canvas.StrokeColor = color; canvas.StrokeSize = 1.5f;
        canvas.DrawRectangle(r);
        canvas.FontColor = Colors.White;
        canvas.FontSize = theme.PriceTagFont;
        canvas.DrawString(text, new RectF(r.X + 3, r.Y, r.Width - 6, r.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    // Trendline readout: a price pill at each endpoint + a midpoint pill with the price change,
    // % change ((p2/p1-1)*100) and the #bars between anchors, tinted green up / red down.
    private void DrawTrendLabels(ICanvas canvas, RenderFrame f, ChartTheme theme,
        DrawingObject d, float x1, float y1, float x2, float y2,
        Color lineColor, CurrencyType cur)
    {
        canvas.SaveState();
        canvas.StrokeDashPattern = null;
        // Endpoint prices — anchor each pill on the outer side of the segment.
        bool leftIsP1 = x1 <= x2;
        DrawEndpointPriceTag(canvas, theme, f.Plot, x1, y1, d.P1, lineColor, cur, toLeft: leftIsP1);
        DrawEndpointPriceTag(canvas, theme, f.Plot, x2, y2, d.P2, lineColor, cur, toLeft: !leftIsP1);

        // Midpoint change / % / bars, coloured by sign.
        decimal change = d.P2 - d.P1;
        double pct = d.P1 != 0m ? ((double)(d.P2 / d.P1) - 1.0) * 100.0 : 0.0;
        int bars = f.Bucket > TimeSpan.Zero
            ? (int)Math.Round(Math.Abs((d.T2 - d.T1).TotalSeconds) / f.Bucket.TotalSeconds)
            : 0;
        var tint = change >= 0m ? theme.Bull : theme.Bear;
        string sign = change >= 0m ? "+" : "";
        string text = $"{sign}{CurrencyHelper.Format(change, cur)}  ({sign}{pct:0.00}%)  {bars} bar{(bars == 1 ? "" : "s")}";

        float w = Math.Max(120f, text.Length * 6.2f);
        float cx = (x1 + x2) * 0.5f;
        float cy = (y1 + y2) * 0.5f;
        float lx = Math.Clamp(cx - w / 2f, f.Plot.Left, Math.Max(f.Plot.Left, f.Plot.Right - w));
        float ly = Math.Clamp(cy - 28f, f.Plot.Top, Math.Max(f.Plot.Top, f.Plot.Bottom - 16f));
        var panel = new RectF(lx, ly, w, 16f);
        canvas.FillColor = tint;
        canvas.FillRectangle(panel);
        canvas.FontColor = Colors.White;
        canvas.FontSize = theme.PriceTagFont;
        canvas.DrawString(text, new RectF(panel.X + 4, panel.Y, panel.Width - 8, panel.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.RestoreState();
    }

    private void DrawHandle(ICanvas canvas, ChartTheme theme, float x, float y, Color color)
    {
        canvas.FillColor = color;
        canvas.FillCircle(x, y, DrawHandleR);
        canvas.StrokeColor = theme.Outline();
        canvas.StrokeSize = 1f;
        canvas.DrawCircle(x, y, DrawHandleR);
    }

    // Uniform cubic B-SPLINE (approximating) over PIXEL control points: the points are CONTROL points and
    // the curve is pulled TOWARD them (it does NOT pass through the interior ones) via a sliding 4-point
    // window. The first + last control points are tripled so the stroke is CLAMPED to its ends. smoothing<=0
    // (or <3 points) falls back to a raw polyline. Round caps/joins so a thick stroke reads as a smooth
    // ribbon (no spiky corners). Pixel-space so callers can trim a terminal back to a head base.
    private static void DrawFreehandPath(ICanvas canvas, IReadOnlyList<PointF> raw, float smoothing)
    {
        int n = raw.Count;
        if (n == 0) return;

        var path = new PathF();
        if (Math.Clamp(smoothing, 0f, 1f) <= 0f || n < 3)
        {
            path.MoveTo(raw[0]);
            for (int i = 1; i < n; i++) path.LineTo(raw[i]);
        }
        else
        {
            var c = new PointF[n + 4];
            c[0] = c[1] = raw[0];
            for (int i = 0; i < n; i++) c[i + 2] = raw[i];
            c[n + 2] = c[n + 3] = raw[n - 1];

            // Each window → a Bézier (C2-continuous): on-curve endpoint (a+4b+d)/6, handles (2b+d)/3, (b+2d)/3.
            static PointF OnCurve(PointF a, PointF b, PointF d)
                => new((a.X + 4f * b.X + d.X) / 6f, (a.Y + 4f * b.Y + d.Y) / 6f);

            path.MoveTo(OnCurve(c[0], c[1], c[2]));
            for (int i = 1; i + 2 < c.Length; i++)
            {
                var b1 = new PointF((2f * c[i].X + c[i + 1].X) / 3f, (2f * c[i].Y + c[i + 1].Y) / 3f);
                var b2 = new PointF((c[i].X + 2f * c[i + 1].X) / 3f, (c[i].Y + 2f * c[i + 1].Y) / 3f);
                var b3 = OnCurve(c[i], c[i + 1], c[i + 2]);
                path.CurveTo(b1, b2, b3);
            }
        }

        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(path);
        canvas.StrokeLineCap = LineCap.Butt;
        canvas.StrokeLineJoin = LineJoin.Miter;
    }
}
