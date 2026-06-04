using System.Globalization;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed class CandleChartDrawable : IDrawable
{
    #region Properties
    public IReadOnlyList<Candle> Candles { get; set; } = Array.Empty<Candle>();
    public double YPaddingPercent { get; set; } = 0.06;
    public double XPaddingPercent { get; set; } = 0.02;

    // Visible time-range window. When valid, drives X-axis scaling so empty
    // pre-history / post-future space is rendered cleanly. Falls back to
    // first/last candle times when not set (legacy behavior).
    public ChartViewport Viewport { get; set; } = ChartViewport.Empty;

    // Y-axis behaviour. When YAutoFit is true the range is recomputed each
    // paint from the visible candles (default). When false, the drawable
    // either uses the explicit ManualYMin/Max range, or — if those are unset —
    // freezes at the most recent computed range so the chart doesn't jump.
    public bool YAutoFit { get; set; } = true;
    public decimal? ManualYMin { get; set; }
    public decimal? ManualYMax { get; set; }

    // Crosshair overlay driven by ChartView's pointer-move handler. When
    // Crosshair.Visible is false the crosshair pass is skipped.
    public CrosshairState Crosshair { get; set; }

    // Moving-average overlays. Each series carries its own color and a
    // pre-computed list of points against the candle buffer.
    public IReadOnlyList<MovingAverageSeries> MaSeries { get; set; } = Array.Empty<MovingAverageSeries>();

    // Price marker lines drawn across the chart at user-chosen prices. The
    // currently-dragged marker (if any) is rendered with extra emphasis.
    public IReadOnlyList<PriceMarker> Markers { get; set; } = Array.Empty<PriceMarker>();
    public Guid? DraggingMarkerId { get; set; }

    // Set by ChartView while the user drags an open-order line. The line whose
    // OrderId matches DraggingOrderId is drawn at DraggingOrderPrice instead of
    // its stored price so the user sees the level follow the cursor live.
    public int? DraggingOrderId { get; set; }
    public decimal? DraggingOrderPrice { get; set; }

    // The user's open limit orders for the visible stock+currency, rendered as
    // dashed horizontal lines tagged with the side and quantity. Drawn before
    // the live-price line so the live tag stays the most prominent.
    public IReadOnlyList<OpenOrderLine> OpenOrderLines { get; set; } = Array.Empty<OpenOrderLine>();

    // Current live price; when set, drawn as a horizontal price line and tag in the right gutter.
    public decimal? CurrentPrice { get; set; }

    // Palette — populated by ChartView at construction time. Defaults are intentionally stark so a
    // missing resource is obvious rather than silently themed.
    public Color Bg = Colors.Black;
    public Color Axis = Colors.Gray;
    public Color Grid = Colors.DimGray;
    public Color Bull = Colors.Green;
    public Color Bear = Colors.Red;
    public Color PriceLineUp = Colors.Green;
    public Color PriceLineDown = Colors.Red;
    public Color CrosshairColor = Colors.LightGray;
    public Color MarkerColor = Colors.Goldenrod;

    // Open-order line colours per side. Defaults to Bull/Bear (green/red, the
    // Binance + TradingView convention) but the ViewModel can override either
    // slot from a user-facing color picker.
    public Color OpenOrderBuyColor = Colors.Green;
    public Color OpenOrderSellColor = Colors.Red;
    // §3.6 P3: armed-stop trigger lines paint in a muted amber so they read distinctly
    // from the green/red resting-limit lines, regardless of buy/sell direction.
    public Color OpenOrderStopColor = Color.FromArgb("#E0A030");

    // The user's executed fills, drawn as small triangles (buy up/below, sell down/above).
    public IReadOnlyList<FillMarker> FillMarkers { get; set; } = Array.Empty<FillMarker>();
    // Deliberately a similar-but-distinct shade from the Bull/Bear candle colours so a fill
    // marker reads as "my trade" rather than blending into the candle it sits against.
    public Color FillBuyColor = Color.FromArgb("#26C281");   // teal-green vs the candle bull green
    public Color FillSellColor = Color.FromArgb("#E74C3C");  // softer red vs the candle bear red

    // Volume bar controls. ShowVolume gates rendering. OverlayVolume picks the
    // TradingView-style overlay where bars sit at low alpha in the bottom strip
    // of the price plot; setting it to false falls back to a separate sub-pane
    // below the plot. Tints default to derivations of Bull/Bear when transparent.
    public bool ShowVolume { get; set; } = true;
    public bool OverlayVolume { get; set; } = true;
    public Color VolumeBullTint = Colors.Transparent;
    public Color VolumeBearTint = Colors.Transparent;

    public float AxisFont = 10f;
    public float PriceTagFont = 10f;

    // Layout paddings — leave gutters for the Y labels (right) and time labels (bottom).
    const float RightAxisW = 64f;   // reserve space for Y labels
    const float BottomAxisH = 24f;  // reserve space for time labels
    const float TopPad = 6f;
    const float LeftPad = 6f;
    const float VolumePaneRatio = 0.18f;  // 18% of total plot height
    const float VolumePaneGap = 4f;
    const float VolumePaneMinChartHeight = 80f;  // skip volume pane on tiny charts
    #endregion

    #region IDrawable Implementation
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();
        canvas.FillColor = Bg;
        canvas.FillRectangle(dirtyRect);

        // Plot rectangle inside the axes — computed even when no candles are visible
        // so the user still sees axes/grid in panned-past-edge regions. When the
        // volume pane is enabled and OverlayVolume is false we split the plot into
        // a price area (top) and a volume area (bottom); when OverlayVolume is true
        // (TradingView-style) the plot keeps its full height and volume bars are
        // drawn at low alpha inside the bottom strip of the same rectangle.
        var fullPlot = ComputePlotRect(dirtyRect);
        RectF plot = fullPlot;
        RectF volRect = default;
        if (ShowVolume && fullPlot.Height >= VolumePaneMinChartHeight)
        {
            float volH = fullPlot.Height * VolumePaneRatio;
            if (OverlayVolume)
            {
                // Bars share the bottom strip of the price plot. plot is unchanged
                // so candle/MA scaling uses the full chart height.
                volRect = new RectF(fullPlot.X, fullPlot.Bottom - volH,
                                    fullPlot.Width, volH);
            }
            else
            {
                plot = new RectF(fullPlot.X, fullPlot.Y,
                                 fullPlot.Width, fullPlot.Height - volH - VolumePaneGap);
                volRect = new RectF(fullPlot.X, plot.Bottom + VolumePaneGap,
                                    fullPlot.Width, volH);
            }
        }
        _lastPlot = plot;
        _lastVolRect = volRect;

        // Visible time-range. Prefer the explicit viewport when set so that
        // empty pre-history / post-future space scales correctly. Fall back
        // to the candle slice extremes for legacy callers.
        DateTime tMin, tMax;
        if (Viewport.IsValid)
        {
            tMin = Viewport.ViewStart;
            tMax = Viewport.ViewEnd;
        }
        else if (Candles.Count > 0)
        {
            tMin = Candles[0].OpenTime;
            tMax = Candles[^1].CloseTime;
            if (tMax <= tMin) tMax = tMin.AddSeconds(1);
            var xPadFallback = TimeSpan.FromTicks((long)((tMax - tMin).Ticks * Math.Max(0, XPaddingPercent)));
            tMax += xPadFallback;
        }
        else
        {
            DrawNoData(canvas, dirtyRect);
            canvas.RestoreState();
            return;
        }

        double spanSec = (tMax - tMin).TotalSeconds;
        if (spanSec <= 0) { canvas.RestoreState(); return; }

        double yMin, yMax;
        if (YAutoFit)
        {
            // Visible price-range across the candle slice. When the slice is empty
            // (panned fully into a gap) we fall back to a sensible neutral range.
            decimal low, high;
            if (Candles.Count > 0)
            {
                low = Candles[0].Low;
                high = Candles[0].High;
                for (int i = 1; i < Candles.Count; i++)
                {
                    var c = Candles[i];
                    if (c.Low < low) low = c.Low;
                    if (c.High > high) high = c.High;
                }
            }
            else if (CurrentPrice is decimal cpFallback)
            {
                low = cpFallback;
                high = cpFallback;
            }
            else
            {
                low = 0m;
                high = 1m;
            }

            // Ensure the live price is always inside the visible range
            if (CurrentPrice is decimal cp)
            {
                if (cp < low) low = cp;
                if (cp > high) high = cp;
            }
            if (high <= low) high = low + 1m;

            // Add top/bottom padding so candles don't hug the plot edges
            var yPad = (double)(high - low) * Math.Max(0, YPaddingPercent);
            yMin = (double)low - yPad;
            yMax = (double)high + yPad;
        }
        else if (ManualYMin is decimal mn && ManualYMax is decimal mx && mx > mn)
        {
            yMin = (double)mn;
            yMax = (double)mx;
        }
        else
        {
            // Manual mode without an explicit range yet — freeze at the most
            // recent auto-fit values so the chart doesn't jump on toggle.
            yMin = _lastYMin;
            yMax = _lastYMax;
        }

        if (yMax <= yMin) yMax = yMin + 1.0;
        _lastYMin = yMin;
        _lastYMax = yMax;
        _lastTMin = tMin;
        _lastTMax = tMax;

        // Coordinate transforms from data-space to plot-space.
        float X(DateTime utc) => plot.Left + (float)(((utc - tMin).TotalSeconds / spanSec) * plot.Width);
        float Y(double price) => plot.Bottom - (float)(((price - yMin) / (yMax - yMin)) * plot.Height);

        var currency = Candles.Count > 0 ? Candles[0].CurrencyType : CurrencyType.USD;

        DrawYGridAndLabels(canvas, plot, yMin, yMax, currency);
        DrawXGridAndLabels(canvas, plot, tMin, tMax);

        // Overlay-mode volume renders BEFORE candles so the bars sit visually
        // behind them (TradingView style). Sub-pane mode renders after the
        // border below so it lives in its own panel.
        if (volRect.Height > 0 && OverlayVolume)
            DrawVolume(canvas, volRect, X);

        DrawCandles(canvas, plot, X, Y);
        DrawMovingAverages(canvas, plot, tMin, tMax, yMin, yMax, X, Y);
        DrawOpenOrderLines(canvas, plot, Y, currency);
        DrawFillMarkers(canvas, plot, X, Y);
        DrawCurrentPriceLine(canvas, plot, Y, currency, tMin, tMax);
        DrawMarkers(canvas, plot, Y, currency);

        // Border around the plot area.
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(plot);

        if (volRect.Height > 0 && !OverlayVolume)
            DrawVolume(canvas, volRect, X);

        // Crosshair sits on top of everything else so it stays visible against candles.
        DrawCrosshair(canvas, plot, currency, X);

        canvas.RestoreState();
    }

    /// <summary>
    /// Draw a dashed horizontal line at each open-order price.  TradingView /
    /// Binance convention:
    ///   - Right-gutter tag shows the price (matching the live-price tag style),
    ///     filled with the side colour and white-on-coloured text.
    ///   - On-chart inline label "B 10" / "S 10" sits just inside the right edge
    ///     of the plot so the user sees side + quantity at a glance without the
    ///     numbers clashing with the gridline price tags.
    /// </summary>
    private void DrawOpenOrderLines(ICanvas canvas, RectF plot, Func<double, float> Y, CurrencyType cur)
    {
        if (OpenOrderLines.Count == 0) return;
        canvas.SaveState();
        for (int i = 0; i < OpenOrderLines.Count; i++)
        {
            var line = OpenOrderLines[i];
            bool dragging = DraggingOrderId == line.OrderId;
            decimal price = dragging && DraggingOrderPrice is decimal dp ? dp : line.Price;
            float y = Y((double)price);
            if (y < plot.Top || y > plot.Bottom) continue;

            // §3.6 P3: a stop trigger line is amber with a tighter dash so it stands apart
            // from the green/red resting-limit lines; limits keep the {4,4} dash + side colour.
            var color = line.IsStop ? OpenOrderStopColor : (line.IsBuy ? OpenOrderBuyColor : OpenOrderSellColor);
            canvas.StrokeColor = color;
            canvas.StrokeSize = dragging ? 2f : 1f;
            canvas.StrokeDashPattern = line.IsStop ? new float[] { 2f, 3f } : new float[] { 4f, 4f };
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            canvas.StrokeDashPattern = null;

            // Right-gutter tag: price, like a TradingView order line.
            var tagRect = new RectF(plot.Right + 1, y - 8, RightAxisW - 2, 16);
            canvas.FillColor = color;
            canvas.FillRectangle(tagRect);
            canvas.FontColor = Colors.White;
            canvas.FontSize = PriceTagFont;
            canvas.DrawString(CurrencyHelper.Format(price, cur),
                new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);

            // Inline label hugged to the right edge of the plot, on top of the dashed
            // line. Small pill so it stays readable against candles. §3.6 P3: a stop reads
            // STOP/STOP-LIM + qty; a resting limit reads B/S + qty.
            var labelText = line.IsStop
                ? $"{(line.IsStopLimit ? "STOP-LIM" : "STOP")} {line.Quantity}"
                : $"{(line.IsBuy ? "B" : "S")} {line.Quantity}";
            float labelW = Math.Max(28f, labelText.Length * 7f);
            var labelRect = new RectF(plot.Right - labelW - 4f, y - 8, labelW, 16);
            canvas.FillColor = color;
            canvas.FillRectangle(labelRect);
            canvas.FontColor = Colors.White;
            canvas.DrawString(labelText, labelRect,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        canvas.RestoreState();
    }

    /// <summary>
    /// Draw the user's executed fills as small, tall triangles at (fill time, fill price).
    /// A buy is an up-pointing triangle sitting just BELOW the price (apex pointing up at it);
    /// a sell is a down-pointing triangle just ABOVE the price (apex pointing down at it). The
    /// base is short and the sides long so the markers read as crisp arrows, not blobs.
    /// </summary>
    private void DrawFillMarkers(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y)
    {
        if (FillMarkers.Count == 0) return;
        const float baseHalf = 4f;  // half the (short) base → 8px wide
        const float height = 16f;   // long sides → tall arrow
        const float gap = 5f;       // offset between the apex and the fill price
        canvas.SaveState();
        for (int i = 0; i < FillMarkers.Count; i++)
        {
            var m = FillMarkers[i];
            float x = X(m.AtTime);
            if (x < plot.Left || x > plot.Right) continue;
            float yPrice = Y((double)m.Price);
            if (yPrice < plot.Top || yPrice > plot.Bottom) continue;

            var path = new PathF();
            if (m.IsBuy)
            {
                // Up triangle below the price: apex (top) points up toward the fill.
                float apexY = yPrice + gap;
                float baseY = apexY + height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
                canvas.FillColor = FillBuyColor;
            }
            else
            {
                // Down triangle above the price: apex (bottom) points down toward the fill.
                float apexY = yPrice - gap;
                float baseY = apexY - height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
                canvas.FillColor = FillSellColor;
            }
            canvas.FillPath(path);
        }
        canvas.RestoreState();
    }

    private void DrawMarkers(ICanvas canvas, RectF plot, Func<double, float> Y, CurrencyType cur)
    {
        if (Markers.Count == 0) return;
        canvas.SaveState();
        for (int i = 0; i < Markers.Count; i++)
        {
            var m = Markers[i];
            float y = Y((double)m.Price);
            if (y < plot.Top || y > plot.Bottom) continue;

            bool dragging = DraggingMarkerId == m.Id;
            canvas.StrokeColor = MarkerColor;
            canvas.StrokeSize = dragging ? 2f : 1f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);

            // Tag in the right gutter — last 14 px reserved as the close hit-zone.
            var tagRect = new RectF(plot.Right + 1, y - 8, RightAxisW - 2, 16);
            canvas.FillColor = MarkerColor;
            canvas.FillRectangle(tagRect);
            canvas.FontColor = Colors.Black;
            canvas.FontSize = PriceTagFont;
            canvas.DrawString(CurrencyHelper.Format(m.Price, cur),
                new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 18, tagRect.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);
            canvas.DrawString("✕",
                new RectF(tagRect.Right - 14, tagRect.Y, 12, tagRect.Height),
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        canvas.RestoreState();
    }

    /// <summary>
    /// Returns the marker hit by the pointer (within 4 px of the line), and whether
    /// the close glyph in the right-gutter tag was clicked.
    /// </summary>
    public (PriceMarker Marker, bool CloseHit)? HitMarker(PointF pInControl)
    {
        if (Markers.Count == 0) return null;
        if (_lastPlot.Width <= 0 || _lastYMax <= _lastYMin) return null;

        for (int i = 0; i < Markers.Count; i++)
        {
            var m = Markers[i];
            float y = (float)(_lastPlot.Bottom - ((double)m.Price - _lastYMin) / (_lastYMax - _lastYMin) * _lastPlot.Height);
            if (Math.Abs(pInControl.Y - y) > 4f) continue;

            // The whole tag-strip width counts as the marker; the rightmost ~14 px
            // is the close hit-zone.
            float closeLeft = _lastPlot.Right + 1 + (RightAxisW - 16);
            bool closeHit = pInControl.X >= closeLeft && pInControl.X <= _lastPlot.Right + RightAxisW;
            bool inLine = pInControl.X >= _lastPlot.Left && pInControl.X <= _lastPlot.Right + RightAxisW;
            if (!inLine) continue;

            return (m, closeHit);
        }
        return null;
    }

    /// <summary>
    /// Returns the open-order line hit by the pointer (within 4 px of the line).
    /// Covers the full width from the plot left edge to the right-gutter tag.
    /// </summary>
    public OpenOrderLine? HitOpenOrderLine(PointF pInControl)
    {
        if (OpenOrderLines.Count == 0) return null;
        if (_lastPlot.Width <= 0 || _lastYMax <= _lastYMin) return null;

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
            float y = (float)(_lastPlot.Bottom
                              - ((double)price - _lastYMin) / (_lastYMax - _lastYMin)
                                * _lastPlot.Height);
            // Skip lines whose price is currently outside the visible Y range —
            // they aren't drawn, so they shouldn't be hit-testable either.
            if (y < _lastPlot.Top || y > _lastPlot.Bottom) continue;
            if (Math.Abs(pInControl.Y - y) > 4f) continue;
            if (pInControl.X < _lastPlot.Left
                || pInControl.X > _lastPlot.Right + RightAxisW) continue;
            return line;
        }
        return null;
    }

    private void DrawMovingAverages(ICanvas canvas, RectF plot,
        DateTime tMin, DateTime tMax, double yMin, double yMax,
        Func<DateTime, float> X, Func<double, float> Y)
    {
        if (MaSeries.Count == 0) return;

        // MaPoint.AtTime is OpenTime; shift to candle centre so the line tracks
        // the middle of each bucket rather than the left edge.
        var halfBucket = Viewport.IsValid
            ? TimeSpan.FromTicks(Viewport.Bucket.Ticks / 2)
            : TimeSpan.Zero;

        canvas.SaveState();
        canvas.StrokeSize = 1.5f;
        foreach (var series in MaSeries)
        {
            var pts = series.Points;
            if (pts == null || pts.Count == 0) continue;

            canvas.StrokeColor = series.Color;
            var path = new PathF();
            bool started = false;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var t = p.AtTime + halfBucket;
                if (t < tMin) continue;
                if (t > tMax) break;
                if (p.Value < yMin || p.Value > yMax) { started = false; continue; }

                float px = X(t);
                float py = Y(p.Value);
                if (!started) { path.MoveTo(px, py); started = true; }
                else path.LineTo(px, py);
            }
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    private void DrawVolume(ICanvas canvas, RectF volRect, Func<DateTime, float> X)
    {
        // Sub-pane mode draws a border so the panel reads as distinct. Overlay
        // mode shares the price plot's rectangle so a second border would just
        // cut a horizontal line across the chart.
        if (!OverlayVolume)
        {
            canvas.StrokeColor = Grid;
            canvas.StrokeSize = 1f;
            canvas.DrawRectangle(volRect);
        }

        if (Candles.Count == 0) return;

        long maxVol = 0L;
        for (int i = 0; i < Candles.Count; i++)
            if (Candles[i].Volume > maxVol) maxVol = Candles[i].Volume;
        if (maxVol <= 0) return;

        // Overlay mode lowers alpha further so candles drawn on top stay legible.
        float overlayAlpha = OverlayVolume ? 0.35f : 0.6f;
        var bullTint = VolumeBullTint.Alpha > 0 ? VolumeBullTint : Bull.WithAlpha(overlayAlpha);
        var bearTint = VolumeBearTint.Alpha > 0 ? VolumeBearTint : Bear.WithAlpha(overlayAlpha);

        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float xOpen = X(c.OpenTime);
            float xClose = X(c.CloseTime);
            float cx = (xOpen + xClose) * 0.5f;
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen) * 0.7f);

            float h = (float)(c.Volume / (double)maxVol * volRect.Height);
            if (h < 1f) h = c.Volume > 0 ? 1f : 0f;

            canvas.FillColor = c.Close >= c.Open ? bullTint : bearTint;
            canvas.FillRectangle(cx - bodyW / 2f, volRect.Bottom - h, bodyW, h);
        }
    }

    /// <summary>
    /// Layout helper exposed so view-side code (pointer hit-testing, alert drag)
    /// can map control-space pixels into the plot area without re-implementing
    /// the gutter math.
    /// </summary>
    public RectF ComputePlotRect(RectF dirtyRect) =>
        new(dirtyRect.X + LeftPad,
            dirtyRect.Y + TopPad,
            Math.Max(1f, dirtyRect.Width - RightAxisW - LeftPad),
            Math.Max(1f, dirtyRect.Height - BottomAxisH - TopPad));

    // Geometry cached at the end of Draw() so the inverse transforms below stay
    // consistent with the most recent paint, even between frames.
    private RectF _lastPlot;
    private RectF _lastVolRect;
    private double _lastYMin;
    private double _lastYMax = 1.0;
    private DateTime _lastTMin;
    private DateTime _lastTMax;

    /// <summary>Rectangle reserved for the volume sub-pane in the most recent paint.</summary>
    public RectF VolumeRect => _lastVolRect;
    public double LastYMin => _lastYMin;
    public double LastYMax => _lastYMax;

    private void DrawNoData(ICanvas canvas, RectF r)
    {
        canvas.FontSize = 12f;
        canvas.FontColor = Axis;
        canvas.DrawString("No data", r, HorizontalAlignment.Center, VerticalAlignment.Center);
    }
    #endregion

    #region Axes and Grid
    private void DrawYGridAndLabels(ICanvas canvas, RectF plot, double yMin, double yMax, CurrencyType cur)
    {
        var (niceMin, niceMax, step) = NiceRange(yMin, yMax, maxTicks: 6);
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;

        for (double v = niceMin; v <= niceMax + 1e-9; v += step)
        {
            float y = plot.Bottom - (float)((v - yMin) / (yMax - yMin) * plot.Height);

            // Horizontal grid line
            canvas.StrokeColor = Grid; canvas.StrokeSize = 1f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);

            // Label in the right gutter, aligned to the gridline.
            var label = CurrencyHelper.Format((decimal)v, cur);
            canvas.DrawString(label,
                new RectF(plot.Right + 4, y - 7, RightAxisW - 8, 14),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    private void DrawXGridAndLabels(ICanvas canvas, RectF plot, DateTime tMin, DateTime tMax)
    {
        TimeSpan step = ChooseTimeStep(tMin, tMax, targetTicks: 7);
        var first = AlignToStep(tMin, step, forward: true);

        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;

        double total = (tMax - tMin).TotalSeconds;
        if (total <= 0) return;

        for (var t = first; t <= tMax; t = t.Add(step))
        {
            float x = plot.Left + (float)(((t - tMin).TotalSeconds / total) * plot.Width);

            // Vertical grid line
            canvas.DrawLine(x, plot.Top, x, plot.Bottom);

            // Show full date when crossing midnight, otherwise just time
            string text = (t.TimeOfDay == TimeSpan.Zero)
                ? t.ToLocalTime().ToString("ddd dd MMM", CultureInfo.InvariantCulture)
                : t.ToLocalTime().ToString("HH:mm");

            canvas.DrawString(text, new RectF(x - 40, plot.Bottom + 2, 80, BottomAxisH - 2),
                HorizontalAlignment.Center, VerticalAlignment.Top);
        }
    }
    #endregion

    #region Candle Drawing
    private void DrawCandles(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y)
    {
        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float xOpen = X(c.OpenTime);
            float xClose = X(c.CloseTime);
            float cx = (xOpen + xClose) * 0.5f;
            // Body takes 70% of the candle slot, leaving gaps between adjacent bars.
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen) * 0.7f);

            float yOpen = Y((double)c.Open);
            float yClose = Y((double)c.Close);
            float yHigh = Y((double)c.High);
            float yLow = Y((double)c.Low);

            bool bull = c.Close >= c.Open;
            var bodyColor = bull ? Bull : Bear;

            // Wick takes the body colour so the candle reads as one shape.
            canvas.StrokeColor = bodyColor;
            canvas.StrokeSize = 1f;
            canvas.DrawLine(cx, yHigh, cx, yLow);

            // Body — clamp height to 1px minimum so doji candles are still visible.
            float top = Math.Min(yOpen, yClose);
            float h = Math.Max(1f, Math.Abs(yClose - yOpen));
            canvas.FillColor = bodyColor;
            canvas.FillRectangle(cx - bodyW / 2f, top, bodyW, h);
        }
    }

    private void DrawCurrentPriceLine(ICanvas canvas, RectF plot, Func<double, float> Y, CurrencyType cur,
        DateTime tMin, DateTime tMax)
    {
        if (CurrentPrice is not decimal price) return;
        if (Candles.Count == 0) return;

        // Don't draw when the user has scrolled "now" out of view (either fully into
        // history past the right edge, or way out into synthetic future-space).
        var now = TimeHelper.NowUtc();
        if (now < tMin || now > tMax) return;

        float y = Y((double)price);
        // Skip drawing if the price falls outside the visible plot.
        if (y < plot.Top || y > plot.Bottom) return;

        // Colour matches the direction of the most recent candle.
        var last = Candles[^1];
        bool up = last.Close >= last.Open;
        var color = up ? PriceLineUp : PriceLineDown;

        // Dashed horizontal line across the plot.
        canvas.SaveState();
        canvas.StrokeColor = color;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 4f, 3f };
        canvas.DrawLine(plot.Left, y, plot.Right, y);
        canvas.StrokeDashPattern = null;
        canvas.RestoreState();

        // Price tag in the right gutter, drawn as a filled pill with white text.
        var label = CurrencyHelper.Format(price, cur);
        var tagRect = new RectF(plot.Right + 1, y - 8, RightAxisW - 2, 16);
        canvas.FillColor = color;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(label,
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
    }
    #endregion

    #region Hit-testing (public — used by ChartView pointer handlers)
    /// <summary>
    /// Maps a Y pixel inside the plot back to a price using the cached Y-range
    /// from the most recent paint. Returns null if no successful paint has been
    /// performed yet.
    /// </summary>
    public decimal? PixelToPrice(float yInControl)
    {
        if (_lastPlot.Height <= 0 || _lastYMax <= _lastYMin) return null;
        if (yInControl < _lastPlot.Top || yInControl > _lastPlot.Bottom) return null;
        double frac = (_lastPlot.Bottom - yInControl) / (double)_lastPlot.Height;
        double price = _lastYMin + frac * (_lastYMax - _lastYMin);
        return (decimal)price;
    }

    /// <summary>
    /// Maps an X pixel inside the plot back to a UTC time using the cached time
    /// range from the most recent paint.
    /// </summary>
    public DateTime PixelToTime(float xInControl)
    {
        if (_lastPlot.Width <= 0 || _lastTMax <= _lastTMin) return _lastTMin;
        double frac = (xInControl - _lastPlot.Left) / (double)_lastPlot.Width;
        frac = Math.Clamp(frac, 0.0, 1.0);
        var span = _lastTMax - _lastTMin;
        return _lastTMin.AddTicks((long)(span.Ticks * frac));
    }

    /// <summary>
    /// Returns the index into <see cref="Candles"/> whose bucket contains the X
    /// pixel, or null if the pointer falls into empty pre-history / future space.
    /// Accepts pointer positions inside the price pane or the volume sub-pane —
    /// both share the same time axis.
    /// </summary>
    public int? HitCandleIndex(PointF pInControl)
    {
        if (Candles.Count == 0) return null;
        if (pInControl.X < _lastPlot.Left || pInControl.X > _lastPlot.Right) return null;
        bool inPrice = pInControl.Y >= _lastPlot.Top && pInControl.Y <= _lastPlot.Bottom;
        bool inVol = _lastVolRect.Height > 0
                     && pInControl.Y >= _lastVolRect.Top
                     && pInControl.Y <= _lastVolRect.Bottom;
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
        if (_lastPlot.Contains(pInControl)) return true;
        return _lastVolRect.Height > 0 && _lastVolRect.Contains(pInControl);
    }

    /// <summary>
    /// True when the pointer is over the right-hand Y-axis gutter — the strip
    /// to the right of the price plot reserved for price labels. Wheel events
    /// here zoom the Y axis instead of the X axis.
    /// </summary>
    public bool IsInYAxisGutter(PointF pInControl)
    {
        return pInControl.X > _lastPlot.Right
            && pInControl.X <= _lastPlot.Right + RightAxisW
            && pInControl.Y >= _lastPlot.Top
            && pInControl.Y <= _lastPlot.Bottom;
    }
    #endregion

    #region Crosshair drawing
    private void DrawCrosshair(ICanvas canvas, RectF plot, CurrencyType cur, Func<DateTime, float> X)
    {
        if (!Crosshair.Visible) return;
        if (Crosshair.X < plot.Left || Crosshair.X > plot.Right) return;

        // Pointer can sit in the price area or the volume sub-pane. The vertical
        // line spans both; the horizontal line is drawn within whichever pane the
        // pointer is in, and the price tag only appears for the price pane.
        bool inPrice = Crosshair.Y >= plot.Top && Crosshair.Y <= plot.Bottom;
        bool inVol = _lastVolRect.Height > 0
                     && Crosshair.Y >= _lastVolRect.Top
                     && Crosshair.Y <= _lastVolRect.Bottom;
        if (!inPrice && !inVol) return;

        // Snap vertical line to the centre of the hovered candle when there is one.
        float vx = Crosshair.X;
        if (Crosshair.CandleIndex is int idx && idx >= 0 && idx < Candles.Count)
        {
            var c = Candles[idx];
            vx = (X(c.OpenTime) + X(c.CloseTime)) * 0.5f;
        }

        // Vertical line spans the full chart, including the volume sub-pane.
        float vyTop = plot.Top;
        float vyBottom = _lastVolRect.Height > 0 ? _lastVolRect.Bottom : plot.Bottom;

        canvas.SaveState();
        canvas.StrokeColor = CrosshairColor;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 3f, 3f };
        canvas.DrawLine(vx, vyTop, vx, vyBottom);
        canvas.DrawLine(plot.Left, Crosshair.Y, plot.Right, Crosshair.Y);
        canvas.StrokeDashPattern = null;

        // Price tag in the right gutter — only when the crosshair Y is in the price pane.
        if (inPrice && PixelToPrice(Crosshair.Y) is decimal price)
        {
            var tagRect = new RectF(plot.Right + 1, Crosshair.Y - 8, RightAxisW - 2, 16);
            canvas.FillColor = CrosshairColor;
            canvas.FillRectangle(tagRect);
            canvas.FontColor = Colors.Black;
            canvas.FontSize = PriceTagFont;
            canvas.DrawString(CurrencyHelper.Format(price, cur),
                new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        // Time tag on the bottom axis at the crosshair X.
        var t = PixelToTime(vx);
        var timeText = t.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture);
        var timeRect = new RectF(vx - 50, vyBottom + 2, 100, BottomAxisH - 2);
        canvas.FillColor = CrosshairColor;
        canvas.FillRectangle(timeRect);
        canvas.FontColor = Colors.Black;
        canvas.FontSize = AxisFont;
        canvas.DrawString(timeText, timeRect, HorizontalAlignment.Center, VerticalAlignment.Center);

        canvas.RestoreState();
    }
    #endregion

    #region Private Helpers
    // Returns a human-friendly axis range and tick step that neatly covers [min, max].
    private static (double niceMin, double niceMax, double step) NiceRange(double min, double max, int maxTicks)
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
    private static TimeSpan ChooseTimeStep(DateTime from, DateTime to, int targetTicks)
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
    private static DateTime AlignToStep(DateTime t, TimeSpan step, bool forward)
    {
        var ticks = step.Ticks;
        long k = t.Ticks / ticks;
        long aligned = forward
            ? ((t.Ticks % ticks) == 0 ? t.Ticks : (k + 1) * ticks)
            : k * ticks;
        return new DateTime(aligned, DateTimeKind.Utc);
    }
    #endregion
}
