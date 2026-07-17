using System.IO;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Storage;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KieshStockExchange.Views.TradePageViews;

public partial class ChartView : ContentView
{
    private readonly CandleChartDrawable _drawable = new();
    private ChartViewModel? _vm;
    private readonly IThemeService? _theme;
    private readonly IOrderEditService? _editService;

    // Pan-gesture tracking
    private int _panLastDelta;

    // Active platform-pointer drag (Windows-side). Cross-platform so the
    // (cross-platform) PanGestureRecognizer can suppress its updates while a
    // marker / Y-axis / free-pan drag is in flight.
    private enum DragMode { None, OpenOrder, YAxis, FreePan, Measure, Drawing }
    private DragMode _dragMode = DragMode.None;

    // Inertial-pan state. During a free-pan drag we smooth a velocity (candles/sec)
    // from the recent cursor motion; on release the chart coasts on a dispatcher
    // timer, decaying the velocity each tick until it drops below a floor.
    private IDispatcherTimer? _inertiaTimer;
    private double _panVelocity;        // smoothed candles/sec while free-panning
    private double _inertiaResidual;    // fractional-candle carry between coast ticks
    private long _panLastSampleMs;
    private float _panLastSampleX;
    private const double InertiaDecay = 0.92;     // per ~16 ms tick
    private const double InertiaMinVel = 3.0;     // candles/sec floor to start / keep coasting
    private const double InertiaTickSec = 0.016;
    private const long InertiaStaleMs = 60;       // ignore velocity if the last sample is older

    // Drawing-drag state — set when _dragMode == Drawing. Which part is being moved (endpoint vs
    // whole shape), the drawing snapshot + data-space grab point at press (so a body move applies
    // an absolute delta), whether this drag created a brand-new drawing, and whether the pointer
    // actually travelled (a click that never moved leaves a degenerate trendline we discard).
    private Guid? _draggingDrawingId;
    private DrawingHitPart _draggingDrawingPart;
    private DrawingObject _drawDragOrig;
    private DateTime _drawDragStartTime;
    private decimal _drawDragStartPrice;
    private PointF _drawDragStartPixel;
    private bool _drawDragIsNew;
    private bool _drawDragMoved;

    // Polyline-building state — the Polyline tool drops a vertex per left-click and finishes on a
    // double-click. _polyBuilding gates the append/preview path; _polyPoints accumulates the vertices
    // in data space; _lastPolyClickMs powers manual double-click detection (WinUI's is unreliable here).
    private bool _polyBuilding;
    private readonly List<DrawPoint> _polyPoints = new();
    private long _lastPolyClickMs;
    private const long PolyDoubleClickMs = 400;

    // Open-order-drag state — set when _dragMode == OpenOrder.
    private int? _draggingOrderId;
    private decimal _draggingOrderStartPrice;
    private float _draggingOrderStartY;

    // Minimum vertical pixel travel before a press-release counts as a drag.
    // Below this we treat the gesture as a tap and skip opening the modal.
    private const float OpenOrderDragThresholdPx = 4f;

    // Y-axis-drag state — captured at press, kept constant for the whole drag
    // so the math anchors to where the user clicked (matching TradingView).
    private float _yAxisStartY;
    private double _yAxisStartMin;
    private double _yAxisStartMax;
    private double _yAxisAnchorPrice;
    private float _yAxisPlotHeight;

    // Free-pan state — captured at press, used to compute the new viewport
    // absolutely each frame so the start data-point tracks the cursor exactly.
    private PointF _freePanStartCursor;
    private int _freePanStartOffset;
    private double _freePanStartYMin;
    private double _freePanStartYMax;
    private double _freePanPxPerCandle;
    private double _freePanPricePerPixel;

    public ChartView()
    {
        InitializeComponent();

        ApplyChartPalette();
        Chart.Drawable = _drawable;
        ChartPan.PanUpdated += OnPanUpdated;
        ChartPointer.PointerMoved += OnChartPointerMoved;
        ChartPointer.PointerExited += OnChartPointerExited;

        // Re-pull the chart palette and repaint when the user switches theme,
        // so candles/grid/axis colors follow the active theme dictionary
        // alongside the GraphicsView's DynamicResource-driven BackgroundColor.
        _theme = Application.Current?.Handler?.MauiContext?.Services?.GetService<IThemeService>();
        if (_theme != null)
            _theme.ThemeChanged += OnThemeChanged;

        // Watch the modify-edit service so the dragged price line stays at its
        // dragged-to value while the modify panel is open, and snaps back (or to
        // the new DB price after a successful confirm) once edit mode ends.
        _editService = Application.Current?.Handler?.MauiContext?.Services?.GetService<IOrderEditService>();
        if (_editService != null)
            _editService.PropertyChanged += OnEditServiceChanged;

        Unloaded += OnUnloaded;

#if WINDOWS
        Chart.HandlerChanged += OnChartHandlerChanged;
#endif
    }

    private void OnThemeChanged(object? sender, string newKey)
    {
        ApplyChartPalette();
        Chart.Invalidate();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        StopInertia();
        if (_theme != null) _theme.ThemeChanged -= OnThemeChanged;
        if (_editService != null) _editService.PropertyChanged -= OnEditServiceChanged;
    }

    // Inertial pan: coast OffsetFromLatest on a dispatcher timer, decaying the
    // release velocity each tick. Kept cross-platform (MAUI dispatcher timer) so
    // OnUnloaded can stop it; it's only ever armed from the Windows pan handler.
    private void StartInertia(double velocityCandlesPerSec)
    {
        StopInertia();
        _panVelocity = velocityCandlesPerSec;
        _inertiaResidual = 0;
        _inertiaTimer = Dispatcher.CreateTimer();
        _inertiaTimer.Interval = TimeSpan.FromMilliseconds(16);
        _inertiaTimer.Tick += OnInertiaTick;
        _inertiaTimer.Start();
    }

    private void StopInertia()
    {
        if (_inertiaTimer is null) return;
        _inertiaTimer.Stop();
        _inertiaTimer.Tick -= OnInertiaTick;
        _inertiaTimer = null;
    }

    private void OnInertiaTick(object? sender, EventArgs e)
    {
        if (_vm == null) { StopInertia(); return; }
        _panVelocity *= InertiaDecay;
        if (Math.Abs(_panVelocity) < InertiaMinVel) { StopInertia(); return; }

        // Accumulate the sub-candle remainder so slow coasts still advance eventually.
        _inertiaResidual += _panVelocity * InertiaTickSec;
        int step = (int)_inertiaResidual;
        if (step == 0) return;
        _inertiaResidual -= step;
        if (_vm.PanCommand.CanExecute(step)) _vm.PanCommand.Execute(step);
        else StopInertia();  // hit an edge — stop coasting rather than spin
    }

    // Keep the drawable's transient drag state synced with the modify panel:
    //   • IsEditing flips false  → clear (Cancel snaps back, Confirm paints at
    //     the refreshed DB price).
    //   • EditingOrder set + no drag in flight → seed DraggingOrderId so the
    //     ✎-button entry point also gets a live price line (not just chart-drag).
    //   • PrefillPrice changes (typed in panel or re-dragged) → move the line.
    private void OnEditServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_editService is null) return;

        if (e.PropertyName == nameof(IOrderEditService.IsEditing))
        {
            if (_editService.IsEditing) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_drawable.DraggingOrderId is null) return;
                _drawable.DraggingOrderId = null;
                _drawable.DraggingOrderPrice = null;
                Chart.Invalidate();
            });
            return;
        }

        if (e.PropertyName == nameof(IOrderEditService.EditingOrder))
        {
            var order = _editService.EditingOrder;
            if (order is null) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_drawable.DraggingOrderId == order.OrderId) return; // already seeded by chart drag
                _drawable.DraggingOrderId = order.OrderId;
                _drawable.DraggingOrderPrice = _editService.PrefillPrice ?? order.Price;
                Chart.Invalidate();
            });
            return;
        }

        if (e.PropertyName == nameof(IOrderEditService.PrefillPrice))
        {
            var order = _editService.EditingOrder;
            var newPrice = _editService.PrefillPrice;
            if (order is null || newPrice is null) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_drawable.DraggingOrderId != order.OrderId) return;
                if (_drawable.DraggingOrderPrice == newPrice) return;
                _drawable.DraggingOrderPrice = newPrice;
                Chart.Invalidate();
            });
            return;
        }
    }

    // Pull the palette from Colors.xaml so the drawable doesn't carry its own hex values.
    // Done once in the constructor — these resources are static across the app.
    private void ApplyChartPalette()
    {
        if (TryGetColor("ChartBg",            out var bg))       _drawable.Bg            = bg;
        if (TryGetColor("ChartAxis",          out var axis))     _drawable.Axis          = axis;
        if (TryGetColor("ChartGrid",          out var grid))     _drawable.Grid          = grid;
        if (TryGetColor("ChartBull",          out var bull))     _drawable.Bull          = bull;
        if (TryGetColor("ChartBear",          out var bear))     _drawable.Bear          = bear;
        if (TryGetColor("ChartPriceLineUp",   out var lineUp))   _drawable.PriceLineUp   = lineUp;
        if (TryGetColor("ChartPriceLineDown", out var lineDown)) _drawable.PriceLineDown = lineDown;
        if (TryGetColor("ChartCrosshair",     out var ch))       _drawable.CrosshairColor = ch;
        if (TryGetColor("ChartMarker",        out var marker))   _drawable.MarkerColor   = marker;
        if (TryGetColor("ChartDrawing",       out var drawing))  _drawable.DrawingColor  = drawing;
        if (TryGetColor("ChartTrigger",       out var trig))     _drawable.TriggerColor  = trig;   // §F2
        if (TryGetColor("ChartPositionLine",  out var posLine))  _drawable.PositionLineColor = posLine;
    }

    private static bool TryGetColor(string key, out Color color)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var raw) == true && raw is Color c)
        {
            color = c;
            return true;
        }
        color = Colors.Transparent;
        return false;
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (_vm != null)
        {
            _vm.RedrawRequested -= OnRedrawRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Drawings.CollectionChanged -= OnVmDrawingsChanged;
        }

        // A VM swap abandons any half-built polyline (its vertices belong to the old context).
        CancelPolyline();

        _vm = BindingContext as ChartViewModel;
        if (_vm == null) return;

        _vm.RedrawRequested += OnRedrawRequested;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Drawings.CollectionChanged += OnVmDrawingsChanged;
        UpdateDrawable();
        Chart.Invalidate();
    }

    // Switching draw tool from the toolbar abandons any in-progress polyline build.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartViewModel.DrawTool) && _polyBuilding)
            CancelPolyline();
    }

    // A wholesale collection Reset means the drawing set was reloaded (stock/currency switch) — the
    // only path that Clears it — so drop any half-built polyline whose anchors belong to the old stock.
    private void OnVmDrawingsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset && _polyBuilding)
            CancelPolyline();
    }

    private void OnRedrawRequested()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateDrawable();
            Chart.Invalidate();
        });
    }

    private void UpdateDrawable()
    {
        if (_vm == null) return;
        _drawable.Candles = _vm.GetVisibleCandles();
        _drawable.Style = _vm.ChartStyle;
        _drawable.ScaleMode = _vm.ScaleMode;
        _drawable.SessionOpenPrice = _vm.SessionOpenPrice;
        _drawable.ShowVolume = _vm.VolumeMode != VolumeMode.Off;
        _drawable.OverlayVolume = _vm.VolumeMode == VolumeMode.Overlay;
        // §market-mood: feed the accumulated Fear/Greed series + pane visibility.
        _drawable.ShowMoodPane = _vm.ShowMoodPane;
        _drawable.MoodSeries = _vm.MoodSeries.ToArray();
        // §depth-overlay: order-book resting-liquidity heatmap (levels are reassigned wholesale, so the
        // VM's list reference is a stable snapshot for the paint).
        _drawable.DepthLevels = _vm.DepthLevels;
        _drawable.ShowDepth = _vm.ShowDepth;
        _drawable.Viewport = _vm.GetViewport();
        _drawable.YPaddingPercent = _vm.YPaddingPercent;
        _drawable.XPaddingPercent = _vm.XPaddingPercent;
        _drawable.CurrentPrice = _vm.GetCurrentPrice();
        _drawable.MaSeries = _vm.BuildEnabledMas(ResolveColor);
        _drawable.YAutoFit = _vm.IsYAutoFit;
        _drawable.ManualYMin = _vm.ManualYMin;
        _drawable.ManualYMax = _vm.ManualYMax;
        // Snapshot markers to a stable array — Draw runs on the UI thread but
        // ObservableCollection enumeration with concurrent edits would still be
        // brittle if a future feature mutates from a different thread.
        _drawable.Drawings = _vm.DrawingsHidden ? System.Array.Empty<DrawingObject>() : _vm.Drawings.ToArray();
        _drawable.SelectedDrawingId = _vm.SelectedDrawingId;
        _drawable.SelectedDrawingIds = _vm.SelectedDrawingIds.ToArray();   // multi-select highlight
        _drawable.BuildingStyle = _vm.DefaultDrawStyle;   // faithful in-progress polyline preview (incl. arrowhead)
        _drawable.OpenOrderLines = _vm.OpenOrderLines.ToArray();
        _drawable.OpenOrderBuyColor  = ResolveColor(_vm.BuyOrderColorOption.Key);
        _drawable.OpenOrderSellColor = ResolveColor(_vm.SellOrderColorOption.Key);
        _drawable.FillMarkers = _vm.FillMarkers.ToArray();
        _drawable.TriggerMarkers = _vm.TriggerMarkers.ToArray();   // §F2
        _drawable.Position = _vm.PositionLine;   // avg-entry line + live unrealized P&L
    }

    // Theme-aware colour lookup used for MA series so the VM stays free of
    // Application.Current and ResourceDictionary plumbing.
    private static Color ResolveColor(string key)
        => TryGetColor(key, out var c) ? c : Colors.White;

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_vm == null) return;
        // A platform-pointer drag (marker, Y-axis, or free-pan) is already
        // owning the input — don't let the cross-platform pan recognizer also
        // slide the chart sideways during it.
        if (_dragMode != DragMode.None) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panLastDelta = 0;
                break;

            case GestureStatus.Running:
            {
                double width = Chart.Width;
                int visible = Math.Max(1, _vm.VisibleCount);
                if (width <= 0) return;

                double pxPerCandle = width / visible;
                if (pxPerCandle <= 0) return;

                // Drag right (positive TotalX) = move into history = increase OffsetFromLatest
                int totalDeltaCandles = (int)Math.Round(e.TotalX / pxPerCandle);
                int step = totalDeltaCandles - _panLastDelta;
                if (step != 0 && _vm.PanCommand.CanExecute(step))
                {
                    _vm.PanCommand.Execute(step);
                    _panLastDelta = totalDeltaCandles;
                }
                break;
            }

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _panLastDelta = 0;
                break;
        }
    }

    private void OnChartPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm == null) return;
        var pos = e.GetPosition(Chart);
        if (pos is null) { ClearHoverOverlay(); return; }

        var p = new PointF((float)pos.Value.X, (float)pos.Value.Y);

        // IsInChartArea covers price + volume panes (both share the time axis).
        if (!_drawable.IsInChartArea(p)) { ClearHoverOverlay(); return; }

        var idx = _drawable.HitCandleIndex(p);
        _vm.HoveredCandle = idx is int i && i >= 0 && i < _drawable.Candles.Count
            ? _drawable.Candles[i]
            : null;
        _drawable.Crosshair = new CrosshairState(true, p.X, p.Y, idx);
        Chart.Invalidate();
    }

    private void OnChartPointerExited(object? sender, PointerEventArgs e) => ClearHoverOverlay();

    /// <summary>
    /// Toolbar Y-Auto button. Toggling auto-fit OFF freezes the chart at the
    /// most recent computed Y range so it doesn't snap. Toggling back ON clears
    /// any manual range and resumes auto-fitting to the visible candles.
    /// </summary>
    private void OnYAutoFitClicked(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        if (_vm.IsYAutoFit)
        {
            // Capture the current auto-fit range so manual mode begins where we
            // are. PixelToPrice would also work but the cached values are direct.
            double min = _drawable.LastYMin;
            double max = _drawable.LastYMax;
            if (max > min) _vm.SetManualYRange((decimal)min, (decimal)max);
            _vm.IsYAutoFit = false;
        }
        else
        {
            _vm.ManualYMin = null;
            _vm.ManualYMax = null;
            _vm.IsYAutoFit = true;
        }
    }

    // §Snapshot (LP1): render the current chart offscreen to a PNG saved in Pictures (fallback: cache)
    // via UP-CORE's ChartSnapshotRenderer, then toast the file name. Deterministic — no gestures.
    private async void OnSnapshotClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_vm is null) return;
            int w = (int)Math.Round(Chart.Width);
            int h = (int)Math.Round(Chart.Height);
            if (w <= 1 || h <= 1) return;   // not laid out yet

            UpdateDrawable();               // reflect the exact current view
            var bg = TryGetColor("ChartBg", out var c) ? c : Colors.Black;
            var png = ChartSnapshotRenderer.Render(_drawable, w, h, bg, scale: 2f, title: SnapshotTitle());

            // Open a native Save-As dialog so the user picks the location/name (CommunityToolkit FileSaver).
            // Default name carries the ticker pairing (e.g. KSE-AAPL-USD-...) so saved charts self-identify.
            var pair = SnapshotPairTag();
            var name = $"KSE-{pair}{DateTime.Now:yyyyMMdd-HHmmss}.png";
            using var stream = new MemoryStream(png);
            var result = await FileSaver.Default.SaveAsync(name, stream, CancellationToken.None);
            if (result.IsSuccessful)
                await Toast.Make($"Snapshot saved: {Path.GetFileName(result.FilePath)}").Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Chart snapshot failed: {ex}");
            try { await Toast.Make("Snapshot failed.").Show(); } catch { /* toast best-effort */ }
        }
    }

    // Build the "SYMBOL-CURRENCY-" filename tag from the current selection (e.g. "AAPL-USD-"). Strips any
    // filesystem-hostile characters and returns "" when nothing is selected so the name still forms.
    private string SnapshotPairTag()
    {
        var sym = _vm?.Selected.Symbol;
        if (string.IsNullOrWhiteSpace(sym)) return string.Empty;
        var ccy = _vm!.Selected.Currency.ToString();
        var raw = $"{sym}-{ccy}";
        var clean = new string(raw.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return clean.Length == 0 ? string.Empty : clean + "-";
    }

    // Human-readable ticker stamped onto the snapshot image (e.g. "AAPL · USD"). Empty when nothing selected.
    private string SnapshotTitle()
    {
        var sym = _vm?.Selected.Symbol;
        if (string.IsNullOrWhiteSpace(sym)) return string.Empty;
        return $"{sym} · {_vm!.Selected.Currency}";
    }

    private void ClearHoverOverlay()
    {
        if (_vm != null) _vm.HoveredCandle = null;
        _drawable.Crosshair = default;
        Chart.Invalidate();
    }

    // Drag the pen/settings panel around the chart by its header row. Translation is layered on top of
    // the panel's anchored layout, so it stays wherever the user drops it until the panel is re-opened.
    private double _penPanStartTX, _penPanStartTY;
    private void OnPenPanelPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _penPanStartTX = PenPanel.TranslationX;
                _penPanStartTY = PenPanel.TranslationY;
                break;
            case GestureStatus.Running:
                PenPanel.TranslationX = _penPanStartTX + e.TotalX;
                PenPanel.TranslationY = _penPanStartTY + e.TotalY;
                break;
        }
    }
}
