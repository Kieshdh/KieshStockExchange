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
    // Series style (the TradingView-style chart-type toggle). Orders/markers/MAs/crosshair
    // all key off the shared X/Y transform, so they work unchanged on every style.
    public ChartStyle Style { get; set; } = ChartStyle.Candles;
    // Y-axis price scale. Log changes the price<->pixel transform (via the shared PriceToFrac/
    // FracToPrice helpers, so hit-testing and gridlines stay consistent); Percent only relabels.
    public PriceScaleMode ScaleMode { get; set; } = PriceScaleMode.Linear;
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

    // Drag-to-measure ruler driven by ChartView's Shift-drag handler. When
    // Measure.Active is false the measure pass is skipped.
    public MeasureState Measure { get; set; }

    // Moving-average overlays. Each series carries its own color and a
    // pre-computed list of points against the candle buffer.
    public IReadOnlyList<MovingAverageSeries> MaSeries { get; set; } = Array.Empty<MovingAverageSeries>();

    // User drawings (horizontal lines + trendlines), anchored in data space so they hold their
    // place through pan/zoom. The currently-dragged drawing (if any) paints with extra emphasis;
    // the selected drawing (if any) shows grab-handles and drives the floating style-bar.
    public IReadOnlyList<DrawingObject> Drawings { get; set; } = Array.Empty<DrawingObject>();
    public Guid? DraggingDrawingId { get; set; }
    public Guid? SelectedDrawingId { get; set; }
    // In-progress Polyline being built (left-clicks drop vertices; a double-click commits). While
    // building, ChartView feeds the dropped vertices + a live cursor point so the render pass draws
    // the accumulated segments plus a rubber-band segment to the cursor. Both null when not building.
    public IReadOnlyList<DrawPoint>? BuildingPolyline { get; set; }
    public DrawPoint? BuildingPolylineCursor { get; set; }
    // Fallback drawing colour (theme-overridable via a ChartDrawing resource) for drawings whose
    // persisted Style has no colour — new drawings carry their own Style.Color from the style-bar.
    public Color DrawingColor = Color.FromArgb("#4C9AFF");

    // Set by ChartView while the user drags an open-order line. The line whose
    // OrderId matches DraggingOrderId is drawn at DraggingOrderPrice instead of
    // its stored price so the user sees the level follow the cursor live.
    public int? DraggingOrderId { get; set; }
    public decimal? DraggingOrderPrice { get; set; }

    // The user's open limit orders for the visible stock+currency, rendered as
    // dashed horizontal lines tagged with the side and quantity. Drawn before
    // the live-price line so the live tag stays the most prominent.
    public IReadOnlyList<OpenOrderLine> OpenOrderLines { get; set; } = Array.Empty<OpenOrderLine>();

    // The user's open position in the visible stock+currency (null when flat), drawn as a
    // solid line at the average entry price with a floating size + live-P&L tag. Distinct from
    // the dashed order/live lines so "my position" reads as its own thing.
    public PositionLine? Position { get; set; }

    // Current live price; when set, drawn as a horizontal price line and tag in the right gutter.
    public decimal? CurrentPrice { get; set; }
    // Session reference price (the current day's open) — when set, the price tag shows the
    // session % change beneath the price, in the up/down colour (the TradingView axis convention).
    public decimal? SessionOpenPrice { get; set; }

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
    // Position line: a teal accent distinct from the green/red order lines and the blue trigger
    // arrows, so the average-entry level reads as "my open position". Theme-overridable via
    // ChartPositionLine. The P&L tag itself paints green/red (Bull/Bear) by profit/loss.
    public Color PositionLineColor = Color.FromArgb("#26C6DA");

    // The user's executed fills, drawn as small triangles (buy up/below, sell down/above).
    public IReadOnlyList<FillMarker> FillMarkers { get; set; } = Array.Empty<FillMarker>();
    // Deliberately a similar-but-distinct shade from the Bull/Bear candle colours so a fill
    // marker reads as "my trade" rather than blending into the candle it sits against.
    public Color FillBuyColor = Color.FromArgb("#26C281");   // teal-green vs the candle bull green
    public Color FillSellColor = Color.FromArgb("#E74C3C");  // softer red vs the candle bear red

    // §F2: fired-trigger activation points, drawn as larger hollow blue arrows at the trigger price
    // (where the trigger crossed) — distinct from the solid green/red fill triangles.
    public IReadOnlyList<TriggerMarker> TriggerMarkers { get; set; } = Array.Empty<TriggerMarker>();
    public Color TriggerColor = Color.FromArgb("#3B82F6");   // theme overrides via ChartTrigger

    // Volume bar controls. ShowVolume gates rendering. OverlayVolume picks the
    // TradingView-style overlay where bars sit at low alpha in the bottom strip
    // of the price plot; setting it to false falls back to a separate sub-pane
    // below the plot. Tints default to derivations of Bull/Bear when transparent.
    public bool ShowVolume { get; set; } = true;
    public bool OverlayVolume { get; set; } = true;
    public Color VolumeBullTint = Colors.Transparent;
    public Color VolumeBearTint = Colors.Transparent;

    // §market-mood: an always-separate sub-pane (below price + volume) plotting the live Fear/Greed series
    // (0..100) the VM accumulates from the server's ground-truth field, against the SAME time axis as the
    // candles. Off by default. The zones reuse Bull/Bear (greed/fear); the line is its own distinct accent.
    public bool ShowMoodPane { get; set; } = false;
    public IReadOnlyList<(DateTime Time, double Value)> MoodSeries { get; set; } = Array.Empty<(DateTime, double)>();
    public Color MoodLineColor = Color.FromArgb("#B39DDB"); // soft violet, distinct from candle/volume palette

    // §depth-overlay: live order-book resting liquidity, drawn as a horizontal histogram anchored at the
    // plot's right edge — each level a thin bar at Y(price) whose length ∝ its quantity (normalized to the
    // snapshot's largest level). Bids paint green / asks red at low alpha so candles stay readable. Off by
    // default; the VM mirrors the book feed's levels here and toggles ShowDepth from the toolbar.
    public bool ShowDepth { get; set; } = false;
    public IReadOnlyList<DepthLevel> DepthLevels { get; set; } = Array.Empty<DepthLevel>();

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
    const float MoodPaneRatio = 0.16f;    // 16% of total plot height for the mood sub-pane
    const float MoodPaneGap = 4f;
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
        // §market-mood: reserve the mood strip off the BOTTOM first (always a separate pane, never overlay),
        // then run the existing volume split against the remaining area so volume behaviour is byte-identical
        // when the mood pane is off (baseArea == fullPlot).
        RectF baseArea = fullPlot;
        RectF moodRect = default;
        if (ShowMoodPane && fullPlot.Height >= VolumePaneMinChartHeight)
        {
            float moodH = fullPlot.Height * MoodPaneRatio;
            moodRect = new RectF(fullPlot.X, fullPlot.Bottom - moodH, fullPlot.Width, moodH);
            baseArea = new RectF(fullPlot.X, fullPlot.Y, fullPlot.Width, fullPlot.Height - moodH - MoodPaneGap);
        }

        RectF plot = baseArea;
        RectF volRect = default;
        if (ShowVolume && baseArea.Height >= VolumePaneMinChartHeight)
        {
            float volH = baseArea.Height * VolumePaneRatio;
            if (OverlayVolume)
            {
                // Bars share the bottom strip of the price plot. plot is unchanged
                // so candle/MA scaling uses the full chart height.
                volRect = new RectF(baseArea.X, baseArea.Bottom - volH,
                                    baseArea.Width, volH);
            }
            else
            {
                plot = new RectF(baseArea.X, baseArea.Y,
                                 baseArea.Width, baseArea.Height - volH - VolumePaneGap);
                volRect = new RectF(baseArea.X, plot.Bottom + VolumePaneGap,
                                    baseArea.Width, volH);
            }
        }
        _lastPlot = plot;
        _lastVolRect = volRect;
        _lastMoodRect = moodRect;

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

            // Tight target = visible candle extremes + an 8% top/bottom margin so candles never
            // hug the plot edges. The hysteresis smoother turns this into the committed axis range.
            double span = (double)(high - low);
            double pad = span * AutoFitMargin;
            (yMin, yMax) = SmoothAutoFit((double)low - pad, (double)high + pad);
        }
        else if (ManualYMin is decimal mn && ManualYMax is decimal mx && mx > mn)
        {
            yMin = (double)mn;
            yMax = (double)mx;
            _autoFitInit = false;   // re-enabling autofit later re-snaps to the live candles
        }
        else
        {
            // Manual mode without an explicit range yet — freeze at the most
            // recent auto-fit values so the chart doesn't jump on toggle.
            yMin = _lastYMin;
            yMax = _lastYMax;
            _autoFitInit = false;
        }

        if (yMax <= yMin) yMax = yMin + 1.0;
        _lastYMin = yMin;
        _lastYMax = yMax;
        _lastTMin = tMin;
        _lastTMax = tMax;

        // Coordinate transforms from data-space to plot-space. Y routes through PriceToFrac so
        // the log scale (and its inverse in PixelToPrice) share one definition.
        float X(DateTime utc) => plot.Left + (float)(((utc - tMin).TotalSeconds / spanSec) * plot.Width);
        float Y(double price) => plot.Bottom - (float)(PriceToFrac(price, yMin, yMax) * plot.Height);

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
        DrawPositionLine(canvas, plot, Y, currency);
        DrawFillMarkers(canvas, plot, X, Y);
        DrawTriggerMarkers(canvas, plot, X, Y);
        DrawCurrentPriceLine(canvas, plot, Y, currency, tMin, tMax);
        DrawDrawings(canvas, plot, X, Y, currency);

        // Border around the plot area.
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(plot);

        if (volRect.Height > 0 && !OverlayVolume)
            DrawVolume(canvas, volRect, X);

        // §market-mood: the Fear/Greed sub-pane, plotted against the same X() time transform as the candles.
        if (moodRect.Height > 0)
            DrawMood(canvas, moodRect, X, tMin, tMax);

        // §depth-overlay: resting-liquidity heatmap in the right portion of the price plot.
        if (ShowDepth)
            DrawDepth(canvas, plot, Y);

        // Crosshair sits on top of everything else so it stays visible against candles.
        DrawCrosshair(canvas, plot, currency, X);
        // Measure ruler sits above the crosshair while a Shift-drag is in flight.
        DrawMeasure(canvas, plot);

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
            // §F12: a dormant bracket child draws in the same colour but at 45 % alpha to convey
            // "not live yet" — the parent hasn't filled, so the leg hasn't armed/rested either.
            var baseColor = line.IsStop ? OpenOrderStopColor : (line.IsBuy ? OpenOrderBuyColor : OpenOrderSellColor);
            var color = line.IsDormant ? baseColor.WithAlpha(0.45f) : baseColor;
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
            // STOP/STOP-LIM + qty; a resting limit reads B/S + qty. §F12: dormant legs label
            // as TP/SL so the user knows they're bracket children, not standalone resting orders.
            var labelText = line.IsStop
                ? $"{(line.IsStopLimit ? "STOP-LIM" : (line.IsDormant ? "SL" : "STOP"))} {line.Quantity}"
                : (line.IsDormant ? $"TP {line.Quantity}" : $"{(line.IsBuy ? "B" : "S")} {line.Quantity}");
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
    /// Draw the user's open position TradingView-style: a solid horizontal line at the average
    /// entry price in the teal position colour, an inline LONG/SHORT + size pill hugged to the
    /// right edge, and a right-gutter tag showing the live unrealized P&L (currency on top, % below)
    /// tinted green when in profit / red when in loss. Skipped when flat or off-screen.
    /// </summary>
    private void DrawPositionLine(ICanvas canvas, RectF plot, Func<double, float> Y, CurrencyType cur)
    {
        if (Position is not PositionLine pos || pos.Quantity == 0m) return;
        float y = Y((double)pos.AvgPrice);
        if (y < plot.Top || y > plot.Bottom) return;

        bool profit = pos.UnrealizedPnl >= 0m;
        var pnlColor = profit ? Bull : Bear;

        canvas.SaveState();
        // Solid line — contrasts with the dashed order / live-price lines.
        canvas.StrokeColor = PositionLineColor;
        canvas.StrokeSize = 1.5f;
        canvas.DrawLine(plot.Left, y, plot.Right, y);

        // Inline size pill in the position colour, hugged to the right edge of the plot.
        bool isLong = pos.Quantity > 0m;
        string sizeText = $"{(isLong ? "LONG" : "SHORT")} {Math.Abs(pos.Quantity):0.####}";
        float sizeW = Math.Max(52f, sizeText.Length * 7f);
        var sizeRect = new RectF(plot.Right - sizeW - 4f, y - 8, sizeW, 16);
        canvas.FillColor = PositionLineColor;
        canvas.FillRectangle(sizeRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(sizeText, sizeRect, HorizontalAlignment.Center, VerticalAlignment.Center);

        // Right-gutter P&L tag: currency on top, % below, tinted by profit/loss.
        var tagRect = new RectF(plot.Right + 1, y - 13, RightAxisW - 2, 26);
        canvas.FillColor = pnlColor;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString($"{(pos.UnrealizedPnl >= 0m ? "+" : "")}{CurrencyHelper.Format(pos.UnrealizedPnl, cur)}",
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, 14f),
            HorizontalAlignment.Left, VerticalAlignment.Top);
        canvas.FontSize = PriceTagFont - 1f;
        canvas.DrawString($"{(pos.UnrealizedPct >= 0 ? "+" : "")}{pos.UnrealizedPct:0.00}%",
            new RectF(tagRect.X + 3, tagRect.Y + 13f, tagRect.Width - 6, 12f),
            HorizontalAlignment.Left, VerticalAlignment.Center);
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
        var outline = OutlineForBackground();
        canvas.SaveState();
        for (int i = 0; i < FillMarkers.Count; i++)
        {
            var m = FillMarkers[i];
            // Snap to the center of the candle that contains the fill time so the arrow lines up
            // with its bar (esp. on higher timeframes) instead of landing between two candles.
            float x = SnapToCandleCenterX(m.AtTime, X);
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
            // Theme-aware outline: keeps the arrow legible against both candles and the background.
            canvas.StrokeColor = outline;
            canvas.StrokeSize = 1f;
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    /// <summary>
    /// §F2: draw each fired trigger as a hollow blue arrow at (activation time, trigger price).
    /// Larger than a fill triangle and outlined-not-filled, so a coincident fill + trigger reads as
    /// two distinct things: "filled here" (solid green/red) vs "trigger crossed here" (blue arrow).
    /// Up for a buy trigger, down for a sell.
    /// </summary>
    private void DrawTriggerMarkers(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y)
    {
        if (TriggerMarkers.Count == 0) return;
        const float baseHalf = 6f;  // wider than the fill triangle (4f)
        const float height = 18f;   // taller too
        const float gap = 6f;
        canvas.SaveState();
        for (int i = 0; i < TriggerMarkers.Count; i++)
        {
            var m = TriggerMarkers[i];
            float x = SnapToCandleCenterX(m.AtTime, X);
            if (x < plot.Left || x > plot.Right) continue;
            float yPrice = Y((double)m.Price);
            if (yPrice < plot.Top || yPrice > plot.Bottom) continue;

            var path = new PathF();
            if (m.IsBuy)
            {
                // Up arrow below the trigger price: apex points up toward the level.
                float apexY = yPrice + gap;
                float baseY = apexY + height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
            }
            else
            {
                // Down arrow above the trigger price: apex points down toward the level.
                float apexY = yPrice - gap;
                float baseY = apexY - height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
            }
            // Hollow: low-alpha blue wash + a bold blue outline, vs the fill triangle's solid fill.
            canvas.FillColor = TriggerColor.WithAlpha(0.22f);
            canvas.FillPath(path);
            canvas.StrokeColor = TriggerColor;
            canvas.StrokeSize = 2f;
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    // Center x of the candle bucket that contains t; falls back to the raw time x when no candle
    // covers it (a fill in a gap / outside the loaded slice).
    private float SnapToCandleCenterX(DateTime t, Func<DateTime, float> X)
    {
        int lo = 0, hi = Candles.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var c = Candles[mid];
            if (t < c.OpenTime) hi = mid - 1;
            else if (t >= c.CloseTime) lo = mid + 1;
            else return (X(c.OpenTime) + X(c.CloseTime)) * 0.5f;
        }
        return X(t);
    }

    // Dark background → light outline, light background → dark outline (relative luminance).
    private Color OutlineForBackground()
    {
        double lum = 0.299 * Bg.Red + 0.587 * Bg.Green + 0.114 * Bg.Blue;
        return lum < 0.5 ? Color.FromRgba(1f, 1f, 1f, 0.85f) : Color.FromRgba(0f, 0f, 0f, 0.85f);
    }

    // Drawing geometry — shared by the render pass and the hit-test so the visible shape and its
    // clickable zones never drift apart.
    const float DrawHandleR = 4f;    // endpoint drag-handle radius
    const float DrawHitTol = 5f;     // extra pixel slack when hit-testing a line/handle
    const float DrawCloseHalf = 7f;  // half-size of the ✕ remove glyph
    const float ArrowSize = 10f;     // barb length of an end-of-line arrowhead

    /// <summary>
    /// Draw the user's horizontal lines + trendlines. HLine spans the plot at its price with a
    /// right-gutter price tag; Trend is a segment with a draggable handle at each end. Both carry a
    /// ✕ remove glyph. Anchored in data space via the shared X/Y transforms, so they hold position
    /// through pan/zoom. Off-screen shapes are clipped by the same range checks the candles use.
    /// </summary>
    private void DrawDrawings(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y, CurrencyType cur)
    {
        if (Drawings.Count == 0) return;
        canvas.SaveState();
        for (int i = 0; i < Drawings.Count; i++)
        {
            var d = Drawings[i];
            bool active = DraggingDrawingId == d.Id;
            bool selected = SelectedDrawingId == d.Id;
            // Per-drawing style (colour/thickness/dash); fall back to the theme colour when a
            // legacy drawing carries no colour. A selected/active line paints a touch thicker.
            var color = d.Style.Color ?? DrawingColor;
            float thickness = d.Style.Thickness > 0f ? d.Style.Thickness : 1.5f;
            canvas.StrokeColor = color;
            canvas.StrokeSize = (active || selected) ? thickness + 1f : thickness;
            canvas.StrokeDashPattern = DashPattern(d.Style.Dash);

            if (d.Kind == DrawTool.HLine)
            {
                float y = Y((double)d.P1);
                if (y < plot.Top || y > plot.Bottom) { canvas.StrokeDashPattern = null; continue; }
                canvas.DrawLine(plot.Left, y, plot.Right, y);
                canvas.StrokeDashPattern = null;

                // Right-gutter price tag in the line's colour, matching the order-line convention.
                DrawGutterPriceTag(canvas, plot, y, d.P1, color, cur);
                DrawCloseGlyph(canvas, plot.Left + 10f, y, color);
                // Selection: grab-handles at the ends so it reads as "editable" like a trendline.
                if (selected)
                {
                    DrawHandle(canvas, plot.Left + 1f, y, color);
                    DrawHandle(canvas, plot.Right - 1f, y, color);
                }
            }
            else if (d.Kind == DrawTool.HRay)
            {
                // Horizontal ray: from the click time rightward to the plot edge at price P1.
                float y = Y((double)d.P1);
                if (y < plot.Top || y > plot.Bottom) { canvas.StrokeDashPattern = null; continue; }
                float x1 = X(d.T1);
                canvas.DrawLine(x1, y, plot.Right, y);
                canvas.StrokeDashPattern = null;
                if (d.Style.Arrow) DrawArrowHead(canvas, plot.Right, y, 1f, 0f, color, ArrowSize);
                DrawGutterPriceTag(canvas, plot, y, d.P1, color, cur);
                DrawHandle(canvas, x1, y, color);
                DrawCloseGlyph(canvas, x1, y - 12f, color);
            }
            else if (d.Kind == DrawTool.Polyline)
            {
                var pts = d.Points;
                if (pts is null || pts.Count == 0) { canvas.StrokeDashPattern = null; continue; }
                float lastX = X(pts[0].T), lastY = Y((double)pts[0].P);
                for (int k = 1; k < pts.Count; k++)
                {
                    float nx = X(pts[k].T), ny = Y((double)pts[k].P);
                    canvas.DrawLine(lastX, lastY, nx, ny);
                    lastX = nx; lastY = ny;
                }
                canvas.StrokeDashPattern = null;
                // Arrowhead on the final segment, pointing along its direction.
                if (d.Style.Arrow && pts.Count >= 2)
                {
                    float px = X(pts[^2].T), py = Y((double)pts[^2].P);
                    DrawArrowHead(canvas, lastX, lastY, lastX - px, lastY - py, color, ArrowSize);
                }
                for (int k = 0; k < pts.Count; k++)
                    DrawHandle(canvas, X(pts[k].T), Y((double)pts[k].P), color);
                DrawCloseGlyph(canvas, X(pts[0].T), Y((double)pts[0].P) - 12f, color);
            }
            else // Trend or Ray (both a two-anchor segment; Ray extends past anchor2 to the plot edge)
            {
                float x1 = X(d.T1), y1 = Y((double)d.P1);
                float x2 = X(d.T2), y2 = Y((double)d.P2);
                float farX = x2, farY = y2;
                if (d.Kind == DrawTool.Ray)
                    (farX, farY) = RayExit(x1, y1, x2 - x1, y2 - y1, plot);
                canvas.DrawLine(x1, y1, farX, farY);
                canvas.StrokeDashPattern = null;
                if (d.Style.Arrow) DrawArrowHead(canvas, farX, farY, farX - x1, farY - y1, color, ArrowSize);
                DrawHandle(canvas, x1, y1, color);
                DrawHandle(canvas, x2, y2, color);
                DrawCloseGlyph(canvas, (x1 + x2) * 0.5f, (y1 + y2) * 0.5f - 12f, color);
                // Trendline labels (always-on v1): endpoint prices + a midpoint change/% / bar-count
                // tag coloured by direction (TradingView convention).
                DrawTrendLabels(canvas, d, x1, y1, x2, y2, color, cur);
            }
            canvas.StrokeDashPattern = null;
        }

        DrawBuildingPolyline(canvas, X, Y);
        canvas.RestoreState();
    }

    // Live preview of the polyline being built: the dropped vertices connected in order, plus a
    // rubber-band segment from the last vertex to the current cursor point. Drawn in the default
    // style so it reads as "in progress" until the double-click commits it.
    private void DrawBuildingPolyline(ICanvas canvas, Func<DateTime, float> X, Func<double, float> Y)
    {
        var pts = BuildingPolyline;
        if (pts is null || pts.Count == 0) return;
        var color = DrawStyle.Default.Color;
        canvas.StrokeColor = color;
        canvas.StrokeSize = DrawStyle.Default.Thickness;
        canvas.StrokeDashPattern = null;
        float lastX = X(pts[0].T), lastY = Y((double)pts[0].P);
        for (int k = 1; k < pts.Count; k++)
        {
            float nx = X(pts[k].T), ny = Y((double)pts[k].P);
            canvas.DrawLine(lastX, lastY, nx, ny);
            lastX = nx; lastY = ny;
        }
        if (BuildingPolylineCursor is DrawPoint c)
            canvas.DrawLine(lastX, lastY, X(c.T), Y((double)c.P));
        for (int k = 0; k < pts.Count; k++)
            DrawHandle(canvas, X(pts[k].T), Y((double)pts[k].P), color);
    }

    // Solid = no pattern; Dash = medium dashes; Dot = tight dots.
    private static float[]? DashPattern(DashKind kind) => kind switch
    {
        DashKind.Dash => new[] { 5f, 4f },
        DashKind.Dot => new[] { 1f, 3f },
        _ => null,
    };

    // Right-gutter price pill (shared by HLine + the trend endpoint tags).
    private void DrawGutterPriceTag(ICanvas canvas, RectF plot, float y, decimal price, Color color, CurrencyType cur)
    {
        var tagRect = new RectF(plot.Right + 1, y - 8, RightAxisW - 2, 16);
        canvas.FillColor = color;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(CurrencyHelper.Format(price, cur),
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    // A small price pill anchored beside a trendline endpoint (flips to the inside edge so it
    // doesn't spill off the plot). Painted in the line's colour with white text.
    private void DrawEndpointPriceTag(ICanvas canvas, RectF plot, float x, float y, decimal price, Color color, CurrencyType cur, bool toLeft)
    {
        string text = CurrencyHelper.Format(price, cur);
        float w = Math.Max(40f, text.Length * 6.5f);
        float lx = toLeft ? x - w - 6f : x + 6f;
        lx = Math.Clamp(lx, plot.Left, Math.Max(plot.Left, plot.Right - w));
        float ly = Math.Clamp(y - 8f, plot.Top, Math.Max(plot.Top, plot.Bottom - 16f));
        var r = new RectF(lx, ly, w, 16f);
        canvas.FillColor = color;
        canvas.FillRectangle(r);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(text, new RectF(r.X + 3, r.Y, r.Width - 6, r.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
    }

    // Trendline readout: a price pill at each endpoint + a midpoint pill with the price change,
    // % change ((p2/p1-1)*100) and the #bars between anchors, tinted green up / red down.
    private void DrawTrendLabels(ICanvas canvas, DrawingObject d, float x1, float y1, float x2, float y2,
        Color lineColor, CurrencyType cur)
    {
        canvas.SaveState();
        canvas.StrokeDashPattern = null;
        // Endpoint prices — anchor each pill on the outer side of the segment.
        bool leftIsP1 = x1 <= x2;
        DrawEndpointPriceTag(canvas, _lastPlot, x1, y1, d.P1, lineColor, cur, toLeft: leftIsP1);
        DrawEndpointPriceTag(canvas, _lastPlot, x2, y2, d.P2, lineColor, cur, toLeft: !leftIsP1);

        // Midpoint change / % / bars, coloured by sign.
        decimal change = d.P2 - d.P1;
        double pct = d.P1 != 0m ? ((double)(d.P2 / d.P1) - 1.0) * 100.0 : 0.0;
        int bars = Viewport.Bucket > TimeSpan.Zero
            ? (int)Math.Round(Math.Abs((d.T2 - d.T1).TotalSeconds) / Viewport.Bucket.TotalSeconds)
            : 0;
        var tint = change >= 0m ? Bull : Bear;
        string sign = change >= 0m ? "+" : "";
        string text = $"{sign}{CurrencyHelper.Format(change, cur)}  ({sign}{pct:0.00}%)  {bars} bar{(bars == 1 ? "" : "s")}";

        float w = Math.Max(120f, text.Length * 6.2f);
        float cx = (x1 + x2) * 0.5f;
        float cy = (y1 + y2) * 0.5f;
        float lx = Math.Clamp(cx - w / 2f, _lastPlot.Left, Math.Max(_lastPlot.Left, _lastPlot.Right - w));
        float ly = Math.Clamp(cy - 28f, _lastPlot.Top, Math.Max(_lastPlot.Top, _lastPlot.Bottom - 16f));
        var panel = new RectF(lx, ly, w, 16f);
        canvas.FillColor = tint;
        canvas.FillRectangle(panel);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(text, new RectF(panel.X + 4, panel.Y, panel.Width - 8, panel.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.RestoreState();
    }

    // Far intersection of the ray (origin + t·dir, t ≥ 0) with the plot rect — the point where the
    // ray leaves the box. Origin is assumed inside; returns origin when the direction is degenerate.
    private static (float x, float y) RayExit(float ox, float oy, float dx, float dy, RectF r)
    {
        float t = float.MaxValue;
        if (dx > 1e-6f) t = Math.Min(t, (r.Right - ox) / dx);
        else if (dx < -1e-6f) t = Math.Min(t, (r.Left - ox) / dx);
        if (dy > 1e-6f) t = Math.Min(t, (r.Bottom - oy) / dy);
        else if (dy < -1e-6f) t = Math.Min(t, (r.Top - oy) / dy);
        if (t == float.MaxValue) t = 0f;
        return (ox + dx * t, oy + dy * t);
    }

    // Filled triangle arrowhead with its tip at (tipX,tipY) pointing along (dirX,dirY); size = barb length.
    private void DrawArrowHead(ICanvas canvas, float tipX, float tipY, float dirX, float dirY, Color color, float size)
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

    private void DrawHandle(ICanvas canvas, float x, float y, Color color)
    {
        canvas.FillColor = color;
        canvas.FillCircle(x, y, DrawHandleR);
        canvas.StrokeColor = OutlineForBackground();
        canvas.StrokeSize = 1f;
        canvas.DrawCircle(x, y, DrawHandleR);
    }

    private void DrawCloseGlyph(ICanvas canvas, float cx, float cy, Color color)
    {
        var r = new RectF(cx - DrawCloseHalf, cy - DrawCloseHalf, DrawCloseHalf * 2, DrawCloseHalf * 2);
        canvas.FillColor = color;
        canvas.FillRectangle(r);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString("✕", r, HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    // Forward data->pixel transforms rebuilt from the last paint's cached geometry, so hit-testing
    // maps a drawing's data anchors back to the exact pixels the render pass used. Y routes through
    // PriceToFrac so the log scale is honoured just like the axes.
    private float PriceToPixelY(decimal price)
        => (float)(_lastPlot.Bottom - PriceToFrac((double)price, _lastYMin, _lastYMax) * _lastPlot.Height);

    private float TimeToPixelX(DateTime t)
    {
        if (_lastTMax <= _lastTMin) return _lastPlot.Left;
        double frac = (t - _lastTMin).TotalSeconds / (_lastTMax - _lastTMin).TotalSeconds;
        return (float)(_lastPlot.Left + frac * _lastPlot.Width);
    }

    /// <summary>
    /// Returns the drawing hit by the pointer and which part (an endpoint, the body, or the ✕
    /// remove glyph). Searches topmost-first so the most recently added drawing wins overlaps.
    /// </summary>
    public (DrawingObject Drawing, DrawingHitPart Part)? HitDrawing(PointF p)
    {
        if (Drawings.Count == 0) return null;
        if (_lastPlot.Width <= 0 || _lastYMax <= _lastYMin) return null;

        for (int i = Drawings.Count - 1; i >= 0; i--)
        {
            var d = Drawings[i];
            if (d.Kind == DrawTool.HLine)
            {
                float y = PriceToPixelY(d.P1);
                if (y < _lastPlot.Top || y > _lastPlot.Bottom) continue;
                if (WithinBox(p, _lastPlot.Left + 10f, y, DrawCloseHalf)) return (d, DrawingHitPart.Close);
                if (p.X >= _lastPlot.Left && p.X <= _lastPlot.Right && Math.Abs(p.Y - y) <= DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.HRay)
            {
                float x1 = TimeToPixelX(d.T1), y = PriceToPixelY(d.P1);
                if (WithinBox(p, x1, y - 12f, DrawCloseHalf)) return (d, DrawingHitPart.Close);
                if (Dist(p.X, p.Y, x1, y) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (p.X >= x1 - DrawHitTol && p.X <= _lastPlot.Right && Math.Abs(p.Y - y) <= DrawHitTol)
                    return (d, DrawingHitPart.Body);
            }
            else if (d.Kind == DrawTool.Polyline)
            {
                var pts = d.Points;
                if (pts is null || pts.Count == 0) continue;
                if (WithinBox(p, TimeToPixelX(pts[0].T), PriceToPixelY(pts[0].P) - 12f, DrawCloseHalf))
                    return (d, DrawingHitPart.Close);
                float lastX = TimeToPixelX(pts[0].T), lastY = PriceToPixelY(pts[0].P);
                for (int k = 1; k < pts.Count; k++)
                {
                    float nx = TimeToPixelX(pts[k].T), ny = PriceToPixelY(pts[k].P);
                    if (PointSegDist(p.X, p.Y, lastX, lastY, nx, ny) <= DrawHitTol)
                        return (d, DrawingHitPart.Body);
                    lastX = nx; lastY = ny;
                }
            }
            else // Trend or Ray
            {
                float x1 = TimeToPixelX(d.T1), y1 = PriceToPixelY(d.P1);
                float x2 = TimeToPixelX(d.T2), y2 = PriceToPixelY(d.P2);
                if (WithinBox(p, (x1 + x2) * 0.5f, (y1 + y2) * 0.5f - 12f, DrawCloseHalf))
                    return (d, DrawingHitPart.Close);
                if (Dist(p.X, p.Y, x1, y1) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor1);
                if (Dist(p.X, p.Y, x2, y2) <= DrawHandleR + DrawHitTol) return (d, DrawingHitPart.Anchor2);
                // Ray body extends past anchor2 to the plot edge — hit-test the full drawn segment.
                float fx = x2, fy = y2;
                if (d.Kind == DrawTool.Ray) (fx, fy) = RayExit(x1, y1, x2 - x1, y2 - y1, _lastPlot);
                if (PointSegDist(p.X, p.Y, x1, y1, fx, fy) <= DrawHitTol
                    && p.X >= _lastPlot.Left - 2 && p.X <= _lastPlot.Right + 2
                    && p.Y >= _lastPlot.Top - 2 && p.Y <= _lastPlot.Bottom + 2)
                    return (d, DrawingHitPart.Body);
            }
        }
        return null;
    }

    private static bool WithinBox(PointF p, float cx, float cy, float half)
        => Math.Abs(p.X - cx) <= half && Math.Abs(p.Y - cy) <= half;

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
    /// §market-mood: draw the accumulated Fear/Greed series in its own sub-pane on a FIXED 0..100 scale.
    /// Red band below 30 (fear) and green band above 70 (greed) wash the zones; 0/50/100 gridlines label
    /// the axis; the line is plotted against the shared X() time transform so it lines up with the candles.
    /// A current-value pill (score + label) sits top-left of the strip.
    /// </summary>
    private void DrawMood(ICanvas canvas, RectF r, Func<DateTime, float> X, DateTime tMin, DateTime tMax)
    {
        // Sub-pane border, matching the volume sub-pane.
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(r);

        float MoodY(double v) => r.Bottom - (float)(Math.Clamp(v, 0, 100) / 100.0 * r.Height);

        // Fear (<30) / greed (>70) zone washes so the strip reads at a glance.
        float y30 = MoodY(30), y70 = MoodY(70);
        canvas.FillColor = Bear.WithAlpha(0.10f);
        canvas.FillRectangle(r.X, y30, r.Width, r.Bottom - y30);
        canvas.FillColor = Bull.WithAlpha(0.10f);
        canvas.FillRectangle(r.X, r.Y, r.Width, y70 - r.Y);

        // 0 / 50 / 100 gridlines + right-gutter labels.
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;
        foreach (var lvl in new[] { 0.0, 50.0, 100.0 })
        {
            float y = MoodY(lvl);
            canvas.StrokeColor = Grid.WithAlpha(0.5f);
            canvas.StrokeSize = 1f;
            canvas.DrawLine(r.Left, y, r.Right, y);
            canvas.DrawString($"{lvl:0}",
                new RectF(r.Right + 4, y - 7, RightAxisW - 8, 14),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        // Accumulated series polyline. Clipped to the visible time window; values map on the fixed scale.
        if (MoodSeries.Count > 0)
        {
            var path = new PathF();
            bool started = false;
            for (int i = 0; i < MoodSeries.Count; i++)
            {
                var (t, v) = MoodSeries[i];
                if (t < tMin || t > tMax) { started = false; continue; }
                float px = X(t), py = MoodY(v);
                if (!started) { path.MoveTo(px, py); started = true; }
                else path.LineTo(px, py);
            }
            canvas.StrokeColor = MoodLineColor;
            canvas.StrokeSize = 1.6f;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);

            // Current-value pill top-left: latest score + fear/greed word, tinted by zone.
            double latest = MoodSeries[^1].Value;
            var (word, tint) = latest >= 70 ? ("Greed", Bull)
                             : latest <= 30 ? ("Fear", Bear)
                             : ("Neutral", MoodLineColor);
            string label = $"Mood {latest:0}  {word}";
            var pill = new RectF(r.Left + 4, r.Top + 3, Math.Max(96f, label.Length * 6.5f), 15f);
            canvas.FillColor = tint.WithAlpha(0.85f);
            canvas.FillRectangle(pill);
            canvas.FontColor = Colors.White;
            canvas.FontSize = AxisFont;
            canvas.DrawString(label, new RectF(pill.X + 4, pill.Y, pill.Width - 6, pill.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    /// <summary>
    /// §depth-overlay: draw the live order-book resting liquidity as a horizontal histogram in the right
    /// portion of the price plot. Each level is a thin bar at Y(price), anchored at the plot's right edge
    /// and extending left by (level.Quantity / maxQuantity) × a fraction of the plot width. Bids paint
    /// green, asks red, both at low alpha so the candles underneath stay legible. Levels whose price is
    /// outside the visible Y range are skipped (they'd land off-plot anyway).
    /// </summary>
    private void DrawDepth(ICanvas canvas, RectF plot, Func<double, float> Y)
    {
        if (DepthLevels.Count == 0) return;

        // Normalize against the largest level so the biggest bar spans MaxDepthBarFrac of the plot width
        // and every other bar scales in proportion — a relative "where's the liquidity" read.
        decimal maxQty = 0m;
        for (int i = 0; i < DepthLevels.Count; i++)
            if (DepthLevels[i].Quantity > maxQty) maxQty = DepthLevels[i].Quantity;
        if (maxQty <= 0m) return;

        const float MaxDepthBarFrac = 0.30f;  // biggest bar reaches 30% of the plot width in from the right
        const float BarHalfH = 1.5f;          // half the bar thickness (px)
        float maxW = plot.Width * MaxDepthBarFrac;

        canvas.SaveState();
        for (int i = 0; i < DepthLevels.Count; i++)
        {
            var lvl = DepthLevels[i];
            float y = Y((double)lvl.Price);
            if (y < plot.Top || y > plot.Bottom) continue;  // off-screen level

            float w = (float)((double)(lvl.Quantity / maxQty) * maxW);
            if (w < 1f) w = 1f;
            // Low alpha keeps price legible; bids green, asks red (Binance/TradingView convention).
            canvas.FillColor = (lvl.IsBid ? Bull : Bear).WithAlpha(0.22f);
            canvas.FillRectangle(plot.Right - w, y - BarHalfH, w, BarHalfH * 2f);
        }
        canvas.RestoreState();
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
    private RectF _lastMoodRect;
    private double _lastYMin;
    private double _lastYMax = 1.0;
    private DateTime _lastTMin;
    private DateTime _lastTMax;

    // Autofit hysteresis state — the committed axis range plus a counter that gates contraction so
    // the range only shrinks after the tight fit has stayed comfortably inside it for a short spell.
    private bool _autoFitInit;
    private double _autoFitLo;
    private double _autoFitHi = 1.0;
    private int _autoFitContractFrames;

    /// <summary>Rectangle reserved for the volume sub-pane in the most recent paint.</summary>
    public RectF VolumeRect => _lastVolRect;
    /// <summary>Rectangle reserved for the mood sub-pane in the most recent paint.</summary>
    public RectF MoodRect => _lastMoodRect;
    /// <summary>Price-plot rectangle from the most recent paint — lets view-side code
    /// (cursor-anchored zoom) map a pointer X to a plot-width fraction without redoing the gutter math.</summary>
    public RectF PlotRect => _lastPlot;
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
    // Price <-> normalized vertical fraction (0 = plot bottom, 1 = plot top), keyed on ScaleMode.
    // Log mode maps equal RATIOS to equal pixels; Linear/Percent are plain linear. PixelToPrice
    // inverts this so hit-testing stays exact under every scale.
    private double PriceToFrac(double price, double lo, double hi)
    {
        if (ScaleMode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return b <= a ? 0.0 : (Math.Log(Math.Max(price, 1e-9)) - a) / (b - a);
        }
        return hi <= lo ? 0.0 : (price - lo) / (hi - lo);
    }

    private double FracToPrice(double frac, double lo, double hi)
    {
        if (ScaleMode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return Math.Exp(a + frac * (b - a));
        }
        return lo + frac * (hi - lo);
    }

    private void DrawYGridAndLabels(ICanvas canvas, RectF plot, double yMin, double yMax, CurrencyType cur)
    {
        var (niceMin, niceMax, step) = NiceRange(yMin, yMax, maxTicks: 6);
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;

        // Percent scale labels tick levels as % change from the leftmost visible bar.
        double? pctRef = ScaleMode == PriceScaleMode.Percent && Candles.Count > 0
            ? (double)Candles[0].Close : (double?)null;

        for (double v = niceMin; v <= niceMax + 1e-9; v += step)
        {
            float y = plot.Bottom - (float)(PriceToFrac(v, yMin, yMax) * plot.Height);
            if (y < plot.Top - 1 || y > plot.Bottom + 1) continue;

            // Horizontal grid line
            canvas.StrokeColor = Grid; canvas.StrokeSize = 1f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);

            // Label in the right gutter, aligned to the gridline.
            string label;
            if (pctRef is double r && r > 0)
            {
                double pc = (v / r - 1.0) * 100.0;
                label = $"{(pc >= 0 ? "+" : "")}{pc:0.0}%";
            }
            else label = CurrencyHelper.Format((decimal)v, cur);

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
        if (Candles.Count == 0) return;

        // Line / Area draw the close series as a polyline (optionally gradient-filled).
        if (Style == ChartStyle.Line || Style == ChartStyle.Area)
        {
            DrawCloseLine(canvas, plot, X, Y, filled: Style == ChartStyle.Area);
            return;
        }

        // Heikin-Ashi smooths the OHLC off the raw buffer; every other style uses raw OHLC.
        // The raw candle is still what feeds the crosshair OHLCV readout (handled in the VM).
        double[]? haO = null, haH = null, haL = null, haC = null;
        if (Style == ChartStyle.HeikinAshi)
            ComputeHeikinAshi(out haO, out haH, out haL, out haC);

        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float xOpen = X(c.OpenTime);
            float xClose = X(c.CloseTime);
            float cx = (xOpen + xClose) * 0.5f;
            // Body takes 70% of the candle slot, leaving gaps between adjacent bars.
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen) * 0.7f);

            double o = haO is null ? (double)c.Open : haO[i];
            double h = haH is null ? (double)c.High : haH[i];
            double l = haL is null ? (double)c.Low : haL[i];
            double cl = haC is null ? (double)c.Close : haC[i];

            float yOpen = Y(o);
            float yClose = Y(cl);
            float yHigh = Y(h);
            float yLow = Y(l);

            bool bull = cl >= o;
            var bodyColor = bull ? Bull : Bear;

            // OHLC bars: high-low stick with a left open-tick and a right close-tick.
            if (Style == ChartStyle.Bars)
            {
                float tick = Math.Max(2f, bodyW * 0.5f);
                canvas.StrokeColor = bodyColor;
                canvas.StrokeSize = 1.4f;
                canvas.DrawLine(cx, yHigh, cx, yLow);
                canvas.DrawLine(cx - tick, yOpen, cx, yOpen);
                canvas.DrawLine(cx, yClose, cx + tick, yClose);
                continue;
            }

            // Wick takes the body colour so the candle reads as one shape.
            canvas.StrokeColor = bodyColor;
            canvas.StrokeSize = 1f;
            canvas.DrawLine(cx, yHigh, cx, yLow);

            // Body — clamp height to 1px minimum so doji candles are still visible.
            float top = Math.Min(yOpen, yClose);
            float bh = Math.Max(1f, Math.Abs(yClose - yOpen));
            var rect = new RectF(cx - bodyW / 2f, top, bodyW, bh);

            // Hollow candles: up bars are outlined (background-filled), down bars stay solid.
            if (Style == ChartStyle.HollowCandles && bull)
            {
                canvas.FillColor = Bg;
                canvas.FillRectangle(rect);
                canvas.StrokeColor = bodyColor;
                canvas.StrokeSize = 1.2f;
                canvas.DrawRectangle(rect);
            }
            else
            {
                canvas.FillColor = bodyColor;
                canvas.FillRectangle(rect);
            }
        }
    }

    // Line / Area: the close-price polyline. Colour follows the visible window's direction
    // (last close vs first close), matching the TradingView line-chart convention.
    private void DrawCloseLine(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y, bool filled)
    {
        var lineColor = Candles[^1].Close >= Candles[0].Close ? Bull : Bear;
        var stroke = new PathF();
        float firstX = 0f, lastX = 0f;
        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float x = (X(c.OpenTime) + X(c.CloseTime)) * 0.5f;
            float y = Y((double)c.Close);
            if (i == 0) { stroke.MoveTo(x, y); firstX = x; }
            else stroke.LineTo(x, y);
            lastX = x;
        }

        if (filled)
        {
            var fill = new PathF();
            for (int i = 0; i < Candles.Count; i++)
            {
                var c = Candles[i];
                float x = (X(c.OpenTime) + X(c.CloseTime)) * 0.5f;
                float y = Y((double)c.Close);
                if (i == 0) fill.MoveTo(x, y);
                else fill.LineTo(x, y);
            }
            fill.LineTo(lastX, plot.Bottom);
            fill.LineTo(firstX, plot.Bottom);
            fill.Close();
            canvas.FillColor = lineColor.WithAlpha(0.12f);
            canvas.FillPath(fill);
        }

        canvas.StrokeColor = lineColor;
        canvas.StrokeSize = 1.6f;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(stroke);
    }

    // Heikin-Ashi transform of the raw buffer. HA_close = avg(O,H,L,C);
    // HA_open = avg(prev HA open, prev HA close); HA high/low extend to the raw extremes.
    private void ComputeHeikinAshi(out double[] o, out double[] h, out double[] l, out double[] c)
    {
        int n = Candles.Count;
        o = new double[n]; h = new double[n]; l = new double[n]; c = new double[n];
        for (int i = 0; i < n; i++)
        {
            var k = Candles[i];
            double ro = (double)k.Open, rh = (double)k.High, rl = (double)k.Low, rc = (double)k.Close;
            double haClose = (ro + rh + rl + rc) / 4.0;
            double haOpen = i == 0 ? (ro + rc) / 2.0 : (o[i - 1] + c[i - 1]) / 2.0;
            o[i] = haOpen;
            c[i] = haClose;
            h[i] = Math.Max(rh, Math.Max(haOpen, haClose));
            l[i] = Math.Min(rl, Math.Min(haOpen, haClose));
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

        // Price tag in the right gutter. When a session reference is set, the tag grows to a
        // second line showing the session % change (TradingView axis convention).
        var label = CurrencyHelper.Format(price, cur);
        bool hasPct = SessionOpenPrice is decimal so && so > 0m;
        float tagH = hasPct ? 26f : 16f;
        var tagRect = new RectF(plot.Right + 1, y - tagH / 2f, RightAxisW - 2, tagH);
        canvas.FillColor = color;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(label,
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, hasPct ? 14f : tagRect.Height),
            HorizontalAlignment.Left, hasPct ? VerticalAlignment.Top : VerticalAlignment.Center);
        if (hasPct)
        {
            double pct = ((double)price / (double)SessionOpenPrice!.Value - 1.0) * 100.0;
            canvas.FontSize = PriceTagFont - 1f;
            canvas.DrawString($"{(pct >= 0 ? "+" : "")}{pct:0.00}%",
                new RectF(tagRect.X + 3, tagRect.Y + 13f, tagRect.Width - 6, 12f),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
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
        double price = FracToPrice(frac, _lastYMin, _lastYMax);
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

    #region Measure ruler drawing
    // Drag-to-measure overlay: a translucent box between the anchor and the cursor plus a
    // label with Δprice, Δ%, Δtime and #bars. Green tint when the end is at/above the start,
    // red when below — the TradingView measure-tool convention. Inverse transforms keep the
    // readout consistent with the axes under every scale mode.
    private void DrawMeasure(ICanvas canvas, RectF plot)
    {
        if (!Measure.Active) return;

        // Clamp the ruler into the plot so the box/label never bleed into the axes gutters.
        float x0 = Math.Clamp(Measure.X0, plot.Left, plot.Right);
        float y0 = Math.Clamp(Measure.Y0, plot.Top, plot.Bottom);
        float x1 = Math.Clamp(Measure.X1, plot.Left, plot.Right);
        float y1 = Math.Clamp(Measure.Y1, plot.Top, plot.Bottom);

        if (PixelToPrice(y0) is not decimal startPrice || PixelToPrice(y1) is not decimal endPrice) return;
        var t0 = PixelToTime(x0);
        var t1 = PixelToTime(x1);

        bool up = endPrice >= startPrice;
        var tint = up ? Bull : Bear;

        // Translucent box + border.
        canvas.SaveState();
        var rect = new RectF(Math.Min(x0, x1), Math.Min(y0, y1),
                             Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        canvas.FillColor = tint.WithAlpha(0.15f);
        canvas.FillRectangle(rect);
        canvas.StrokeColor = tint;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(rect);

        // Readout deltas.
        decimal dPrice = endPrice - startPrice;
        double dPct = startPrice != 0m ? ((double)(endPrice / startPrice) - 1.0) * 100.0 : 0.0;
        var dt = t1 - t0;
        if (dt < TimeSpan.Zero) dt = dt.Negate();
        int bars = Viewport.Bucket > TimeSpan.Zero
            ? (int)Math.Round(dt.TotalSeconds / Viewport.Bucket.TotalSeconds)
            : 0;
        var cur = Candles.Count > 0 ? Candles[0].CurrencyType : CurrencyType.USD;

        string line1 = $"{(dPrice >= 0 ? "+" : "")}{CurrencyHelper.Format(dPrice, cur)}  ({(dPct >= 0 ? "+" : "")}{dPct:0.00}%)";
        string line2 = $"{HumanizeSpan(dt)}  ·  {bars} bar{(bars == 1 ? "" : "s")}";

        // Label panel anchored at the cursor end, flipped to stay inside the plot.
        float panelW = Math.Max(120f, Math.Max(line1.Length, line2.Length) * 6.5f);
        const float panelH = 30f;
        float lx = x1 + 8f;
        if (lx + panelW > plot.Right) lx = x1 - panelW - 8f;
        lx = Math.Clamp(lx, plot.Left, Math.Max(plot.Left, plot.Right - panelW));
        float ly = y1 - panelH - 6f;
        if (ly < plot.Top) ly = y1 + 6f;
        ly = Math.Clamp(ly, plot.Top, Math.Max(plot.Top, plot.Bottom - panelH));
        var panel = new RectF(lx, ly, panelW, panelH);
        canvas.FillColor = tint;
        canvas.FillRectangle(panel);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(line1, new RectF(panel.X + 5, panel.Y + 2, panel.Width - 10, 14),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.DrawString(line2, new RectF(panel.X + 5, panel.Y + 15, panel.Width - 10, 13),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.RestoreState();
    }

    // Human-friendly span for the measure label: coarsest two units that carry signal.
    private static string HumanizeSpan(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }
    #endregion

    #region Private Helpers
    // Autofit smoothing tunables.
    const double AutoFitMargin = 0.08;         // 8% top/bottom breathing room around the candles
    const double AutoFitContractRatio = 0.90;  // contract only when the tight fit < 90% of the range
    const int AutoFitContractHold = 2;         // frames the tight fit must stay small before shrinking (small debounce)
    const double AutoFitContractLerp = 0.60;   // per-frame lerp toward the tight target while shrinking (fast glide)

    // Turns a raw tight [lo,hi] target into a stable committed axis range: expand IMMEDIATELY when
    // candles exceed the range, but contract only after the tight fit has sat < 90% of the range for
    // AutoFitContractHold frames (then lerp in), snapping the bounds to nice tick increments. This
    // kills the per-frame tremble a naive re-fit produces while still tracking real moves.
    private (double lo, double hi) SmoothAutoFit(double targetLo, double targetHi)
    {
        if (targetHi <= targetLo) targetHi = targetLo + 1.0;
        var (snapLo, snapHi) = SnapRange(targetLo, targetHi);   // snap the TARGET once (nice ticks)

        if (!_autoFitInit)
        {
            (_autoFitLo, _autoFitHi) = (snapLo, snapHi);
            _autoFitContractFrames = 0; _autoFitInit = true;
            return (_autoFitLo, _autoFitHi);
        }

        double eps = (_autoFitHi - _autoFitLo) * 1e-4;
        // Expand IMMEDIATELY to cover a breakout (union with the snapped target).
        if (snapLo < _autoFitLo - eps || snapHi > _autoFitHi + eps)
        {
            (_autoFitLo, _autoFitHi) = SnapRange(Math.Min(_autoFitLo, snapLo), Math.Max(_autoFitHi, snapHi));
            _autoFitContractFrames = 0;
            return (_autoFitLo, _autoFitHi);
        }
        // Contract only after the tight fit has sat < ratio·range for a spell, then lerp toward the
        // SNAPPED target (no per-frame re-snap → the bounds actually converge and shrink), snapping exact at the end.
        double curRange = _autoFitHi - _autoFitLo, tgtRange = snapHi - snapLo;
        if (tgtRange < AutoFitContractRatio * curRange) _autoFitContractFrames++; else _autoFitContractFrames = 0;
        if (_autoFitContractFrames >= AutoFitContractHold)
        {
            _autoFitLo += (snapLo - _autoFitLo) * AutoFitContractLerp;
            _autoFitHi += (snapHi - _autoFitHi) * AutoFitContractLerp;
            if (Math.Abs(_autoFitHi - snapHi) < eps && Math.Abs(_autoFitLo - snapLo) < eps)
            { _autoFitLo = snapLo; _autoFitHi = snapHi; _autoFitContractFrames = 0; }
        }
        return (_autoFitLo, _autoFitHi);
    }

    // Quantize a range outward to nice tick increments so the price axis lands on round levels.
    private static (double lo, double hi) SnapRange(double lo, double hi)
    {
        if (hi <= lo) return (lo, lo + 1.0);
        var (niceMin, niceMax, _) = NiceRange(lo, hi, maxTicks: 6);
        return (niceMin, niceMax);
    }

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
