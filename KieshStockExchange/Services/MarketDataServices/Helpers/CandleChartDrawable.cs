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

public sealed partial class CandleChartDrawable : IDrawable
{
    // UP-CORE: the price<->pixel seam the NEW drawing renderers route through (existing renderers keep
    // their local Y closure). Stateless — all plot context is passed per call — so one shared instance.
    private readonly IScaleTransform _scale = new RegularScaleTransform();

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

    // Magnifier box-zoom overlay (transient, no readout). ZoomBox.Active gates it; cleared on release.
    public MeasureState ZoomBox { get; set; }

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
    // True while the in-progress stroke is a FREEHAND drag (continuous) vs a Polyline (click-per-vertex):
    // freehand previews as a bare smooth line — no per-vertex dots (a dot per sample is unusable).
    public bool BuildingIsFreehand { get; set; }
    // The current default pen — so the in-progress polyline preview draws in the exact colour/width/dash
    // AND ending head(s) the committed line will have (a faithful "what you'll get" preview).
    public DrawStyle BuildingStyle { get; set; } = DrawStyle.Default;
    // Fallback drawing colour (theme-overridable via a ChartDrawing resource) for drawings whose
    // persisted Style has no colour — new drawings carry their own Style.Color from the style-bar.
    public Color DrawingColor = Color.FromArgb("#4C9AFF");

    // The full drawing selection set (multi-select via shift-click). A drawing is "selected" (shows
    // handles + thicker stroke) when it is in this set OR is the single SelectedDrawingId primary.
    public IReadOnlyCollection<Guid>? SelectedDrawingIds { get; set; }

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

    // Drawing geometry — the single source shared by DrawingRenderer (drawn handles) and
    // ChartHitTester (clickable zones), injected into both so the visible shape and its
    // hit zone never drift apart.
    const float DrawHandleR = 4f;    // endpoint drag-handle radius
    const float DrawHitTol = 5f;     // extra pixel slack when hit-testing a line/handle
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
            // Bailout still commits the NEW pane rects (old ranges kept) — mirrors the old cache,
            // which wrote the rect fields before this early return.
            _frame = _frame.WithRects(plot, volRect, moodRect);
            DrawNoData(canvas, dirtyRect);
            canvas.RestoreState();
            return;
        }

        double spanSec = (tMax - tMin).TotalSeconds;
        if (spanSec <= 0) { _frame = _frame.WithRects(plot, volRect, moodRect); canvas.RestoreState(); return; }

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
            yMin = _frame.YMin;
            yMax = _frame.YMax;
            _autoFitInit = false;
        }

        if (yMax <= yMin) yMax = yMin + 1.0;

        var currency = Candles.Count > 0 ? Candles[0].CurrencyType : CurrencyType.USD;

        // One frame per paint: pane rects, ranges and every data<->pixel transform committed in a
        // single assignment BEFORE any renderer runs — the old seven-field cache, reified.
        var frame = new RenderFrame(plot, volRect, moodRect, yMin, yMax, tMin, tMax, spanSec,
            ScaleMode, Viewport.Bucket, currency, _scale);
        _frame = frame;
        // Palette snapshot alongside the frame — renderer collaborators read the theme, not the fields.
        var theme = BuildTheme();

        _axisRenderer.DrawYGridAndLabels(canvas, frame, theme, Candles);
        _axisRenderer.DrawXGridAndLabels(canvas, frame, theme);

        // Overlay-mode volume renders BEFORE candles so the bars sit visually
        // behind them (TradingView style). Sub-pane mode renders after the
        // border below so it lives in its own panel.
        if (volRect.Height > 0 && OverlayVolume)
            _indicatorRenderer.DrawVolume(canvas, frame, theme, Candles, OverlayVolume);

        _candleRenderer.DrawCandles(canvas, frame, theme, Candles, Style);
        _indicatorRenderer.DrawMovingAverages(canvas, frame, theme, MaSeries, Viewport.IsValid);
        _overlayRenderer.DrawOpenOrderLines(canvas, frame, theme, OpenOrderLines, DraggingOrderId, DraggingOrderPrice);
        _overlayRenderer.DrawPositionLine(canvas, frame, theme, Position);
        _overlayRenderer.DrawFillMarkers(canvas, frame, theme, FillMarkers, Candles);
        _overlayRenderer.DrawTriggerMarkers(canvas, frame, theme, TriggerMarkers, Candles);
        _candleRenderer.DrawCurrentPriceLine(canvas, frame, theme, Candles, CurrentPrice, SessionOpenPrice);
        _drawingRenderer.DrawDrawings(canvas, frame, theme, Drawings,
            DraggingDrawingId, SelectedDrawingId, SelectedDrawingIds,
            BuildingPolyline, BuildingPolylineCursor, BuildingIsFreehand, BuildingStyle);

        // Border around the plot area.
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(plot);

        if (volRect.Height > 0 && !OverlayVolume)
            _indicatorRenderer.DrawVolume(canvas, frame, theme, Candles, OverlayVolume);

        // §market-mood: the Fear/Greed sub-pane, plotted against the same MapX time transform as the candles.
        if (moodRect.Height > 0)
            _indicatorRenderer.DrawMood(canvas, frame, theme, MoodSeries);

        // §depth-overlay: resting-liquidity heatmap in the right portion of the price plot.
        if (ShowDepth)
            _indicatorRenderer.DrawDepth(canvas, frame, theme, DepthLevels);

        // Crosshair sits on top of everything else so it stays visible against candles.
        _crosshairRenderer.DrawCrosshair(canvas, frame, theme, Crosshair, Candles);
        // Measure ruler sits above the crosshair while a Shift-drag is in flight.
        _measureRenderer.DrawMeasure(canvas, frame, theme, Measure);
        // Magnifier box-zoom overlay (a dashed selection rect) while its drag is in flight.
        _measureRenderer.DrawZoomBox(canvas, frame, theme, ZoomBox);

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

    // The most recent paint's committed geometry + transforms (RenderFrame reifies the old
    // seven-field cache), so hit-testing and the public forwards below stay consistent with the
    // last paint, even between frames.
    private RenderFrame _frame = RenderFrame.Empty;

    // Autofit hysteresis state — the committed axis range plus a counter that gates contraction so
    // the range only shrinks after the tight fit has stayed comfortably inside it for a short spell.
    private bool _autoFitInit;
    private double _autoFitLo;
    private double _autoFitHi = 1.0;
    private int _autoFitContractFrames;

    // Renderer collaborators (Helpers/Drawing) — stateless, fed (canvas, frame, theme, inputs) per paint.
    private readonly MeasureRenderer _measureRenderer = new();
    private readonly AxisRenderer _axisRenderer = new(RightAxisW, BottomAxisH);
    private readonly CandleRenderer _candleRenderer = new(RightAxisW);
    private readonly IndicatorRenderer _indicatorRenderer = new(RightAxisW);
    private readonly OverlayRenderer _overlayRenderer = new(RightAxisW);
    private readonly CrosshairRenderer _crosshairRenderer = new(RightAxisW, BottomAxisH);
    private readonly DrawingRenderer _drawingRenderer = new(DrawHandleR, RightAxisW, BottomAxisH);

    // One cohesive palette snapshot per paint, from the frozen public palette fields above.
    private ChartTheme BuildTheme() => new(
        Bg, Axis, Grid, Bull, Bear, PriceLineUp, PriceLineDown, CrosshairColor,
        OpenOrderBuyColor, OpenOrderSellColor, OpenOrderStopColor, PositionLineColor,
        FillBuyColor, FillSellColor, TriggerColor, VolumeBullTint, VolumeBearTint,
        MoodLineColor, DrawingColor, AxisFont, PriceTagFont);

    /// <summary>Rectangle reserved for the volume sub-pane in the most recent paint.</summary>
    public RectF VolumeRect => _frame.VolRect;
    /// <summary>Rectangle reserved for the mood sub-pane in the most recent paint.</summary>
    public RectF MoodRect => _frame.MoodRect;
    /// <summary>Price-plot rectangle from the most recent paint — lets view-side code
    /// (cursor-anchored zoom) map a pointer X to a plot-width fraction without redoing the gutter math.</summary>
    public RectF PlotRect => _frame.Plot;
    public double LastYMin => _frame.YMin;
    public double LastYMax => _frame.YMax;

    #region Hit-testing (public — used by ChartView pointer handlers)
    // Hit-testing lives in ChartHitTester (Helpers/Drawing) — the drawable forwards the last
    // paint's frame plus the live inputs, so the public surface ChartView consumes is unchanged.
    private readonly ChartHitTester _hitTester = new(DrawHandleR, DrawHitTol, RightAxisW);

    /// <summary>
    /// Returns the drawing hit by the pointer and which part (an endpoint, the body, or the ✕
    /// remove glyph). Searches topmost-first so the most recently added drawing wins overlaps.
    /// </summary>
    public (DrawingObject Drawing, DrawingHitPart Part)? HitDrawing(PointF p)
        => _hitTester.HitDrawing(_frame, Drawings, p);

    /// <summary>
    /// Returns the open-order line hit by the pointer (within 4 px of the line).
    /// Covers the full width from the plot left edge to the right-gutter tag.
    /// </summary>
    public OpenOrderLine? HitOpenOrderLine(PointF pInControl)
        => _hitTester.HitOpenOrderLine(_frame, OpenOrderLines, DraggingOrderId, DraggingOrderPrice, pInControl);

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
        => _hitTester.HitCandleIndex(_frame, Candles, pInControl);

    /// <summary>
    /// True when the control-space pointer falls inside the price area or the
    /// volume sub-pane. Used by ChartView to decide when to hide the crosshair.
    /// </summary>
    public bool IsInChartArea(PointF pInControl)
        => _hitTester.IsInChartArea(_frame, pInControl);

    /// <summary>
    /// True when the pointer is over the right-hand Y-axis gutter — the strip
    /// to the right of the price plot reserved for price labels. Wheel events
    /// here zoom the Y axis instead of the X axis.
    /// </summary>
    public bool IsInYAxisGutter(PointF pInControl)
        => _hitTester.IsInYAxisGutter(_frame, pInControl);
    #endregion

    private void DrawNoData(ICanvas canvas, RectF r)
    {
        canvas.FontSize = 12f;
        canvas.FontColor = Axis;
        canvas.DrawString("No data", r, HorizontalAlignment.Center, VerticalAlignment.Center);
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
        var (niceMin, niceMax, _) = ChartGeometry.NiceRange(lo, hi, maxTicks: 6);
        return (niceMin, niceMax);
    }
    #endregion
}
