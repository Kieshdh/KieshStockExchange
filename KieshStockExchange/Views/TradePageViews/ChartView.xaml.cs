using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Helpers;
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
            _vm.RedrawRequested -= OnRedrawRequested;

        _vm = BindingContext as ChartViewModel;
        if (_vm == null) return;

        _vm.RedrawRequested += OnRedrawRequested;
        UpdateDrawable();
        Chart.Invalidate();
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
        _drawable.Drawings = _vm.Drawings.ToArray();
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

    private void ClearHoverOverlay()
    {
        if (_vm != null) _vm.HoveredCandle = null;
        _drawable.Crosshair = default;
        Chart.Invalidate();
    }

#if WINDOWS
    private void OnChartHandlerChanged(object? sender, EventArgs e)
    {
        if (Chart.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement el)
        {
            el.PointerWheelChanged -= OnPointerWheelChanged;
            el.PointerWheelChanged += OnPointerWheelChanged;

            // Make the GraphicsView keyboard-focusable and grab focus when the
            // pointer enters so arrow keys work without an explicit tab.
            if (el is Microsoft.UI.Xaml.Controls.Control ctrl)
                ctrl.IsTabStop = true;
            el.KeyDown -= OnChartKeyDown;
            el.KeyDown += OnChartKeyDown;
            el.PointerEntered -= OnPlatformPointerEntered;
            el.PointerEntered += OnPlatformPointerEntered;

            el.PointerPressed  -= OnPlatformPointerPressed;
            el.PointerPressed  += OnPlatformPointerPressed;
            el.PointerMoved    -= OnPlatformPointerMoved;
            el.PointerMoved    += OnPlatformPointerMoved;
            el.PointerReleased -= OnPlatformPointerReleased;
            el.PointerReleased += OnPlatformPointerReleased;
            el.PointerCaptureLost -= OnPlatformPointerCaptureLostOrCanceled;
            el.PointerCaptureLost += OnPlatformPointerCaptureLostOrCanceled;
            el.PointerCanceled -= OnPlatformPointerCaptureLostOrCanceled;
            el.PointerCanceled += OnPlatformPointerCaptureLostOrCanceled;
            el.RightTapped     -= OnPlatformRightTapped;
            el.RightTapped     += OnPlatformRightTapped;
        }
    }

    private static PointF PlatformPointerToControl(
        Microsoft.UI.Xaml.UIElement el,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(el).Position;
        return new PointF((float)p.X, (float)p.Y);
    }

    private void OnPlatformPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_vm == null || sender is not Microsoft.UI.Xaml.UIElement el) return;
        // Only react to the primary (left) button — right-click is handled separately.
        var pp = e.GetCurrentPoint(el);
        if (!pp.Properties.IsLeftButtonPressed) return;

        // Any fresh press cancels an in-flight inertial coast (user grabbed the chart again).
        StopInertia();

        var p = PlatformPointerToControl(el, e);

        // Priority 0: Shift-drag starts the measure ruler and owns the gesture, so it
        // never falls through to marker/order/pan. Only inside the chart area (not the gutters).
        bool shiftDown = (Microsoft.UI.Input.InputKeyboardSource
                           .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                           & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (shiftDown && _drawable.IsInChartArea(p))
        {
            _dragMode = DragMode.Measure;
            _drawable.Measure = new MeasureState(true, p.X, p.Y, p.X, p.Y);
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 0.5: an existing drawing (horizontal line / trendline). Grabbable regardless of
        // the active tool so the user can always move or remove one. A ✕ hit removes it outright.
        var drawHit = _drawable.HitDrawing(p);
        if (drawHit is { } dh)
        {
            if (dh.Part == DrawingHitPart.Close)
            {
                _vm.RemoveDrawing(dh.Drawing.Id);
                e.Handled = true;
                return;
            }
            BeginDrawingDrag(dh.Drawing, dh.Part, p, isNew: false);
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 0.6: with a draw tool active, a press in the chart body places a new drawing
        // instead of free-panning. HLine commits on the single click; Trend anchors here and drags
        // its second endpoint to the release point.
        if (_vm.DrawTool != DrawTool.None && _drawable.IsInChartArea(p)
            && _drawable.PixelToPrice(p.Y) is decimal newPrice && newPrice > 0m)
        {
            var t = _drawable.PixelToTime(p.X);
            var id = Guid.NewGuid();
            if (_vm.DrawTool == DrawTool.HLine)
            {
                _vm.AddDrawing(new DrawingObject(id, DrawTool.HLine, t, newPrice, t, newPrice, DrawStyle.Default));
                e.Handled = true;
                return;
            }
            var trend = new DrawingObject(id, DrawTool.Trend, t, newPrice, t, newPrice, DrawStyle.Default);
            _vm.AddDrawing(trend);
            BeginDrawingDrag(trend, DrawingHitPart.Anchor2, p, isNew: true);
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 2: open-order line — drag changes the limit price; on release
        // the Modify Order modal opens (or, if already open for *this* order,
        // its price field updates). A drag on a *different* order while the
        // modify panel is open is suppressed so the visible drag state always
        // matches the order being edited.
        var orderHit = _drawable.HitOpenOrderLine(p);
        if (orderHit is OpenOrderLine ol)
        {
            bool modalOpen = _editService?.IsEditing == true;
            int? editingOrderId = _editService?.EditingOrder?.OrderId;
            bool isSameOrder = modalOpen && editingOrderId == ol.OrderId;

            if (!modalOpen || isSameOrder)
            {
                _dragMode = DragMode.OpenOrder;
                _draggingOrderId = ol.OrderId;
                // Use the drawable's last drawn price when re-dragging mid-edit so
                // the threshold check measures travel since the previous release,
                // not since the original placement.
                _draggingOrderStartPrice = isSameOrder
                    ? (_drawable.DraggingOrderPrice ?? ol.Price)
                    : ol.Price;
                _draggingOrderStartY = p.Y;
                _drawable.DraggingOrderId = ol.OrderId;
                if (!isSameOrder) _drawable.DraggingOrderPrice = ol.Price;
                (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
                Chart.Invalidate();
                e.Handled = true;
                return;
            }
            // else: modal is open for a different order — fall through to other priorities.
        }

        // Priority 3: Y-axis gutter — scales the price range around the click.
        if (_drawable.IsInYAxisGutter(p))
        {
            if (_drawable.LastYMax <= _drawable.LastYMin) return;

            // Auto-flip to manual mode so the next paint doesn't snap back.
            if (_vm.IsYAutoFit)
            {
                _vm.SetManualYRange((decimal)_drawable.LastYMin, (decimal)_drawable.LastYMax);
                _vm.IsYAutoFit = false;
            }

            _dragMode = DragMode.YAxis;
            _yAxisStartY = p.Y;
            _yAxisStartMin = _drawable.LastYMin;
            _yAxisStartMax = _drawable.LastYMax;
            _yAxisPlotHeight = Math.Max(1f, (float)Chart.Height
                                              - 30f /* axes margin guard */);
            // Anchor on the price under the click cursor so it stays put while scaling.
            _yAxisAnchorPrice = _drawable.PixelToPrice(p.Y) is decimal ap
                ? (double)ap
                : (_yAxisStartMin + _yAxisStartMax) * 0.5;
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        // Priority 4: chart body (price + volume area) — free pan that locks
        // the data point under the cursor at drag-start to the cursor.
        if (_drawable.IsInChartArea(p))
        {
            int visible = Math.Max(1, _vm.VisibleCount);
            double pxPerCandle = (double)Chart.Width / visible;
            // Approximate plot height — the drawable's cached price rect height
            // is what really matters; reuse LastYMin/Max + gutters.
            double plotHeight = Math.Max(1.0, Chart.Height - 30.0);
            if (pxPerCandle <= 0 || _drawable.LastYMax <= _drawable.LastYMin) return;

            // Auto-flip to manual mode and seed the range from the current
            // auto-fit values so vertical drag starts at the right place.
            if (_vm.IsYAutoFit)
            {
                _vm.SetManualYRange((decimal)_drawable.LastYMin, (decimal)_drawable.LastYMax);
                _vm.IsYAutoFit = false;
            }

            _dragMode = DragMode.FreePan;
            _freePanStartCursor = p;
            _freePanStartOffset = _vm.OffsetFromLatest;
            _freePanStartYMin = _drawable.LastYMin;
            _freePanStartYMax = _drawable.LastYMax;
            _freePanPxPerCandle = pxPerCandle;
            _freePanPricePerPixel = (_freePanStartYMax - _freePanStartYMin) / plotHeight;
            // Seed inertia velocity sampling from this press.
            _panVelocity = 0;
            _panLastSampleX = p.X;
            _panLastSampleMs = Environment.TickCount64;
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
    }

    private void OnPlatformPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_vm == null || sender is not Microsoft.UI.Xaml.UIElement el) return;
        if (_dragMode == DragMode.None) return;

        var p = PlatformPointerToControl(el, e);

        switch (_dragMode)
        {
            case DragMode.Measure:
                if (_drawable.Measure.Active)
                {
                    _drawable.Measure = _drawable.Measure with { X1 = p.X, Y1 = p.Y };
                    Chart.Invalidate();
                }
                break;

            case DragMode.Drawing:
                DragDrawing(p);
                break;

            case DragMode.OpenOrder:
                if (_draggingOrderId is int oid &&
                    _drawable.PixelToPrice(p.Y) is decimal orderPrice && orderPrice > 0m)
                {
                    _drawable.DraggingOrderPrice = orderPrice;
                    Chart.Invalidate();
                }
                break;

            case DragMode.YAxis:
            {
                // TradingView convention: drag DOWN (dy > 0) = bigger candles
                // (range shrinks), drag UP (dy < 0) = smaller candles (range
                // expands). Anchor on the price under the click cursor.
                float dy = p.Y - _yAxisStartY;
                const double sensitivity = 0.004; // per pixel
                double factor = 1.0 - dy * sensitivity;
                if (factor < 0.1) factor = 0.1;
                else if (factor > 10.0) factor = 10.0;

                double newMin = _yAxisAnchorPrice - (_yAxisAnchorPrice - _yAxisStartMin) * factor;
                double newMax = _yAxisAnchorPrice + (_yAxisStartMax - _yAxisAnchorPrice) * factor;
                if (newMax > newMin)
                    _vm.SetManualYRange((decimal)newMin, (decimal)newMax);
                break;
            }

            case DragMode.FreePan:
            {
                double dx = p.X - _freePanStartCursor.X;
                double dy = p.Y - _freePanStartCursor.Y;

                // X: drag right (dx > 0) = reveal past = bigger OffsetFromLatest.
                int newOffset = _freePanStartOffset + (int)Math.Round(dx / _freePanPxPerCandle);
                if (newOffset != _vm.OffsetFromLatest)
                    _vm.OffsetFromLatest = newOffset;

                // Y: drag down (dy > 0) = both bounds rise = candles move down
                // with the cursor.
                double newMin = _freePanStartYMin + dy * _freePanPricePerPixel;
                double newMax = _freePanStartYMax + dy * _freePanPricePerPixel;
                if (newMax > newMin)
                    _vm.SetManualYRange((decimal)newMin, (decimal)newMax);

                // Sample horizontal velocity (candles/sec) for release-time inertia,
                // exponentially smoothed so a jittery last frame doesn't dominate.
                long nowMs = Environment.TickCount64;
                double dtSec = (nowMs - _panLastSampleMs) / 1000.0;
                if (dtSec > 0 && _freePanPxPerCandle > 0)
                {
                    double instVel = ((p.X - _panLastSampleX) / _freePanPxPerCandle) / dtSec;
                    _panVelocity = _panVelocity * 0.6 + instVel * 0.4;
                    _panLastSampleX = p.X;
                    _panLastSampleMs = nowMs;
                }
                break;
            }
        }
        e.Handled = true;
    }

    private void OnPlatformPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        if (_dragMode == DragMode.OpenOrder)
        {
            var oid = _draggingOrderId;
            var finalPrice = _drawable.DraggingOrderPrice;
            var startY = _draggingOrderStartY;
            var releaseY = sender is Microsoft.UI.Xaml.UIElement uel
                ? PlatformPointerToControl(uel, e).Y
                : startY;
            bool moved = Math.Abs(releaseY - startY) >= OpenOrderDragThresholdPx;
            bool committable = moved && oid.HasValue && finalPrice.HasValue && _vm != null;
            bool modalAlreadyOpen = _editService?.IsEditing == true;

            // Always release pointer capture and reset local drag state. The drawable's
            // DraggingOrderId/Price are kept set whenever a modify is in flight — either
            // we're about to open the modal, or the modal is already open and the user
            // just re-dragged. They get cleared when OnEditServiceChanged sees IsEditing
            // flip back to false (cancel or confirm).
            _draggingOrderId = null;
            _draggingOrderStartPrice = 0m;
            _draggingOrderStartY = 0f;
            if (!committable && !modalAlreadyOpen)
            {
                _drawable.DraggingOrderId = null;
                _drawable.DraggingOrderPrice = null;
            }
            Chart.Invalidate();

            _dragMode = DragMode.None;
            (sender as Microsoft.UI.Xaml.Controls.Control)?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;

            if (committable)
            {
                if (modalAlreadyOpen)
                    _vm!.UpdateModifyPrice(finalPrice!.Value);
                else
                    _ = _vm!.BeginModifyOrderAtAsync(oid!.Value, finalPrice!.Value);
            }
            return;
        }

        if (_dragMode == DragMode.Measure)
        {
            // Clear-on-release (v1): the ruler vanishes when the drag ends.
            _drawable.Measure = default;
            Chart.Invalidate();
        }

        if (_dragMode == DragMode.Drawing)
        {
            // A tool-placed trendline the user clicked without dragging is a degenerate dot — drop
            // it rather than leave an invisible zero-length line on the chart.
            if (_drawDragIsNew && !_drawDragMoved && _draggingDrawingId is Guid nid)
                _vm?.RemoveDrawing(nid);
            _vm?.PersistDrawings();
            _draggingDrawingId = null;
            _drawable.DraggingDrawingId = null;
            Chart.Invalidate();
        }

        if (_dragMode == DragMode.FreePan)
        {
            // Coast only on a genuine flick — a stale sample (paused before release)
            // means the user let go static, so don't fling the chart.
            long sinceMs = Environment.TickCount64 - _panLastSampleMs;
            if (sinceMs <= InertiaStaleMs && Math.Abs(_panVelocity) > InertiaMinVel)
                StartInertia(_panVelocity);
        }

        _dragMode = DragMode.None;
        (sender as Microsoft.UI.Xaml.Controls.Control)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // Pointer capture can be revoked without a paired Released — alt-tab, focus
    // loss, system gestures. Without a reset path the drag state would leak into
    // the next interaction (highlighted line, marker stuck "dragging"). Treat
    // both PointerCaptureLost and PointerCanceled as drag-aborts.
    private void OnPlatformPointerCaptureLostOrCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        _draggingOrderId = null;
        _draggingOrderStartPrice = 0m;
        _draggingOrderStartY = 0f;
        _drawable.DraggingOrderId = null;
        _drawable.DraggingOrderPrice = null;
        _drawable.Measure = default;
        // An aborted new-drawing drag would otherwise leave a degenerate line behind.
        if (_dragMode == DragMode.Drawing && _drawDragIsNew && _draggingDrawingId is Guid nid)
            _vm?.RemoveDrawing(nid);
        _draggingDrawingId = null;
        _drawable.DraggingDrawingId = null;
        _dragMode = DragMode.None;

        Chart.Invalidate();
    }

    // Seed the drawing-drag state from the grabbed drawing and the press point. The data-space grab
    // point lets a whole-shape (body) move apply an absolute delta off the original anchors.
    private void BeginDrawingDrag(DrawingObject d, DrawingHitPart part, PointF p, bool isNew)
    {
        _dragMode = DragMode.Drawing;
        _draggingDrawingId = d.Id;
        _draggingDrawingPart = part;
        _drawDragOrig = d;
        _drawDragStartTime = _drawable.PixelToTime(p.X);
        _drawDragStartPrice = _drawable.PixelToPrice(p.Y) ?? d.P1;
        _drawDragStartPixel = p;
        _drawDragIsNew = isNew;
        _drawDragMoved = false;
        _drawable.DraggingDrawingId = d.Id;
    }

    // Reposition the drawing under the cursor: an endpoint follows the cursor directly; the body
    // shifts by the data-space delta from the press. HLine is horizontal, so only price matters.
    private void DragDrawing(PointF p)
    {
        if (_vm == null || _draggingDrawingId is not Guid id) return;
        if (Math.Abs(p.X - _drawDragStartPixel.X) > OpenOrderDragThresholdPx
            || Math.Abs(p.Y - _drawDragStartPixel.Y) > OpenOrderDragThresholdPx)
            _drawDragMoved = true;

        if (_drawable.PixelToPrice(p.Y) is not decimal price || price <= 0m) return;
        var time = _drawable.PixelToTime(p.X);
        var d = _drawDragOrig;

        DrawingObject upd = _draggingDrawingPart switch
        {
            DrawingHitPart.Anchor1 => d with { T1 = time, P1 = price },
            DrawingHitPart.Anchor2 => d with { T2 = time, P2 = price },
            // Body: HLine just tracks the cursor price; Trend shifts both anchors by the delta.
            _ when d.Kind == DrawTool.HLine => d with { P1 = price },
            _ => ShiftTrend(d, time - _drawDragStartTime, price - _drawDragStartPrice),
        };
        upd = upd with { Id = id };
        _vm.UpdateDrawing(upd);
    }

    private static DrawingObject ShiftTrend(DrawingObject d, TimeSpan dt, decimal dPrice)
        => d with { T1 = d.T1 + dt, P1 = d.P1 + dPrice, T2 = d.T2 + dt, P2 = d.P2 + dPrice };

    private void OnPlatformRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (_vm == null || sender is not Microsoft.UI.Xaml.UIElement el) return;
        var pos = e.GetPosition(el);
        var p = new PointF((float)pos.X, (float)pos.Y);

        // Right-click on a drawing removes it (before the add-line path claims the click).
        if (_drawable.HitDrawing(p) is { } dh)
        {
            _vm.RemoveDrawing(dh.Drawing.Id);
            e.Handled = true;
            return;
        }

        // Right-click on empty chart drops a horizontal line at the cursor price (the old
        // marker gesture, now a styled HLine drawing — muscle memory unchanged).
        if (_drawable.PixelToPrice(p.Y) is decimal mp && mp > 0m)
        {
            var t = _drawable.PixelToTime(p.X);
            _vm.AddDrawing(new DrawingObject(Guid.NewGuid(), DrawTool.HLine, t, mp, t, mp, DrawStyle.Default));
        }
        e.Handled = true;
    }

    private void OnPlatformPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Control ctrl)
            ctrl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void OnChartKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_vm == null) return;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
                       .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                       & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        int step = shift ? 10 : 1;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
                if (_vm.PanCommand.CanExecute(+step)) _vm.PanCommand.Execute(+step);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                if (_vm.PanCommand.CanExecute(-step)) _vm.PanCommand.Execute(-step);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)187:  // OemPlus / "+="
                if (_vm.ZoomInCommand.CanExecute(null)) _vm.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)189:  // OemMinus / "-_"
                if (_vm.ZoomOutCommand.CanExecute(null)) _vm.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Home:
                if (_vm.JumpToOldestCommand.CanExecute(null)) _vm.JumpToOldestCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.End:
                if (_vm.GoLiveCommand.CanExecute(null)) _vm.GoLiveCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_vm == null) return;
        var el = (Microsoft.UI.Xaml.UIElement)sender;
        var pt = e.GetCurrentPoint(el);
        var delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) return;

        bool shift = (Microsoft.UI.Input.InputKeyboardSource
                       .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                       & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        // Y-axis zoom triggers when the pointer is over the right gutter, or
        // when shift is held anywhere on the chart. Convention: wheel up zooms
        // in (smaller range, bigger candles); wheel down zooms out.
        var pCtrl = new PointF((float)pt.Position.X, (float)pt.Position.Y);
        bool inYGutter = _drawable.IsInYAxisGutter(pCtrl);
        if ((shift || inYGutter) && _drawable.LastYMax > _drawable.LastYMin)
        {
            // Auto-fit needs to flip to manual mode the moment the user starts
            // shaping Y manually — otherwise the next paint snaps right back.
            if (_vm.IsYAutoFit)
            {
                _vm.SetManualYRange((decimal)_drawable.LastYMin, (decimal)_drawable.LastYMax);
                _vm.IsYAutoFit = false;
            }

            double curMin = _drawable.LastYMin;
            double curMax = _drawable.LastYMax;
            double centerPrice = (curMin + curMax) * 0.5;
            // Pick a sensible centre: the cursor's price when over the plot,
            // otherwise the midpoint (e.g. when over the gutter outside vertical bounds).
            if (_drawable.PixelToPrice(pCtrl.Y) is decimal pp)
                centerPrice = (double)pp;

            double factor = delta > 0 ? 0.8 : 1.25;
            double newMin = centerPrice - (centerPrice - curMin) * factor;
            double newMax = centerPrice + (curMax - centerPrice) * factor;
            if (newMax > newMin)
                _vm.SetManualYRange((decimal)newMin, (decimal)newMax);
            e.Handled = true;
            return;
        }

        // Cursor-anchored X zoom: keep the time under the cursor pinned to the same
        // pixel by compensating OffsetFromLatest. Fall back to plain zoom when the
        // pointer sits outside the plot (e.g. over the bottom axis) or before first paint.
        var plot = _drawable.PlotRect;
        if (plot.Width > 0 && pCtrl.X >= plot.Left && pCtrl.X <= plot.Right)
        {
            _vm.ZoomAtCursor((pCtrl.X - plot.Left) / plot.Width, delta > 0);
        }
        else if (delta > 0)
        {
            if (_vm.ZoomInCommand.CanExecute(null)) _vm.ZoomInCommand.Execute(null);
        }
        else
        {
            if (_vm.ZoomOutCommand.CanExecute(null)) _vm.ZoomOutCommand.Execute(null);
        }
        e.Handled = true;
    }
#endif
}
