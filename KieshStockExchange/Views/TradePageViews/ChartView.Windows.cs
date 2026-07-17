using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.Views.TradePageViews;

// Windows-only pointer / keyboard / wheel gesture handlers for ChartView (partial). Split out of
// ChartView.xaml.cs so the gesture state machine is reviewable on its own; behaviour unchanged.
public partial class ChartView
{
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

    // Manual double-click detection for the price-axis gutter — WinUI's DoubleTapped is unreliable
    // here because the gutter press handles the pointer + captures it for the Y-scale drag.
    private long _lastGutterClickMs;
    private const long GutterDoubleClickMs = 400;

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
        // Shift-drag on EMPTY chart = measure ruler; shift-click ON a drawing falls through to
        // multi-select (handled in Priority 0.5), so don't start measuring on top of one.
        if (shiftDown && _drawable.IsInChartArea(p) && _drawable.HitDrawing(p) is null)
        {
            _dragMode = DragMode.Measure;
            _drawable.Measure = new MeasureState(true, p.X, p.Y, p.X, p.Y);
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 0.3: the Measure TOOL — drag anywhere in the chart to show the transient ruler. Owns
        // the gesture ahead of the drawing hit-tests so measuring always wins while it's armed. It clears
        // on release and the tool disarms itself (one-shot, like TradingView) — see the release handler.
        if (_vm.DrawTool == DrawTool.Measure && _drawable.IsInChartArea(p))
        {
            _dragMode = DragMode.Measure;
            _drawable.Measure = new MeasureState(true, p.X, p.Y, p.X, p.Y);
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 0.4: a polyline is mid-build — each left-click appends a vertex, a double-click
        // finishes it. Handled ahead of the existing-drawing hit-test so clicks near a prior segment
        // still extend the polyline rather than selecting something underneath.
        if (_polyBuilding && _vm.DrawTool == DrawTool.Polyline && _drawable.IsInChartArea(p)
            && _drawable.PixelToPrice(p.Y) is decimal plp && plp > 0m)
        {
            long nowMs = Environment.TickCount64;
            bool dbl = nowMs - _lastPolyClickMs <= PolyDoubleClickMs;
            _lastPolyClickMs = nowMs;
            if (dbl && _polyPoints.Count >= 2) { CommitPolyline(); e.Handled = true; return; }
            _polyPoints.Add(new DrawPoint(_drawable.PixelToTime(p.X), plp));
            _drawable.BuildingPolyline = _polyPoints.ToList();
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
            // Shift-click TOGGLES the drawing in the multi-selection (for a bulk Delete) — no drag.
            if (shiftDown)
            {
                _vm.AddToSelection(dh.Drawing.Id);
                _vm.IsPenPanelOpen = true;
                Chart.Invalidate();
                e.Handled = true;
                return;
            }
            // Plain tap-to-select: select ONLY this drawing, show its settings, begin a drag. Force the
            // panel open too — re-tapping an already-selected drawing leaves the id unchanged (no OnChanged).
            _vm.SelectSingle(dh.Drawing.Id);
            _vm.IsPenPanelOpen = true;
            BeginDrawingDrag(dh.Drawing, dh.Part, p, isNew: false);
            (el as Microsoft.UI.Xaml.Controls.Control)?.CapturePointer(e.Pointer);
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 0.6: with a draw tool active, a press in the chart body places a new drawing
        // instead of free-panning. HLine/HRay commit on the single click; Trend/Ray anchor here and
        // drag their second endpoint to the release point; Polyline starts a click-per-vertex build.
        if (_vm.DrawTool != DrawTool.None && _drawable.IsInChartArea(p)
            && _drawable.PixelToPrice(p.Y) is decimal newPrice && newPrice > 0m)
        {
            var t = _drawable.PixelToTime(p.X);
            var id = Guid.NewGuid();
            if (_vm.DrawTool == DrawTool.HLine || _vm.DrawTool == DrawTool.HRay || _vm.DrawTool == DrawTool.VLine)
            {
                // One-click lines (HLine/HRay at a price, VLine at a time). After placing: revert to the
                // cursor tool + auto-select the new line so its settings pop up (TradingView one-shot flow).
                _vm.AddDrawing(new DrawingObject(id, _vm.DrawTool, t, newPrice, t, newPrice, _vm.DefaultDrawStyle));
                FinishPlacement(id);
                e.Handled = true;
                return;
            }
            if (_vm.DrawTool == DrawTool.Polyline)
            {
                // Start a new in-progress polyline anchored at the click; subsequent clicks append.
                _polyBuilding = true;
                _polyPoints.Clear();
                _polyPoints.Add(new DrawPoint(t, newPrice));
                _lastPolyClickMs = Environment.TickCount64;
                _drawable.BuildingPolyline = _polyPoints.ToList();
                _drawable.BuildingPolylineCursor = new DrawPoint(t, newPrice);
                Chart.Invalidate();
                e.Handled = true;
                return;
            }
            // Trend or Ray: a two-anchor segment; the second anchor drags to the release point.
            var seg = new DrawingObject(id, _vm.DrawTool, t, newPrice, t, newPrice, _vm.DefaultDrawStyle);
            _vm.AddDrawing(seg);
            // No auto-select — the drag below positions anchor2; the tool row stays visible for the next line.
            BeginDrawingDrag(seg, DrawingHitPart.Anchor2, p, isNew: true);
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

            // Double-click the gutter → reset the Y scale to auto-fit (TradingView reset-scale).
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastGutterClickMs <= GutterDoubleClickMs)
            {
                _lastGutterClickMs = 0;
                _vm.ManualYMin = null;
                _vm.ManualYMax = null;
                _vm.IsYAutoFit = true;
                Chart.Invalidate();
                e.Handled = true;
                return;
            }
            _lastGutterClickMs = nowMs;

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
        // the data point under the cursor at drag-start to the cursor. A press on
        // empty chart (nothing hit above) also clears any drawing selection.
        if (_drawable.IsInChartArea(p))
        {
            _vm.ClearDrawingSelection();
            int visible = Math.Max(1, _vm.VisibleCount);
            double pxPerCandle = (double)Chart.Width / visible;
            // Approximate plot height — the drawable's cached price rect height
            // is what really matters; reuse LastYMin/Max + gutters.
            double plotHeight = Math.Max(1.0, Chart.Height - 30.0);
            if (pxPerCandle <= 0 || _drawable.LastYMax <= _drawable.LastYMin) return;

            // Body-drag pans time and (when already manual) price, but must NOT leave autofit:
            // panning is an X gesture, so Y keeps auto-fitting. Only the Y-gutter drag / Y-zoom /
            // toolbar toggle flip to manual. The move handler skips the vertical drag while autofit.

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

        // Polyline build has no active drag mode — rubber-band the pending segment to the cursor.
        if (_polyBuilding && _vm.DrawTool == DrawTool.Polyline)
        {
            var pc = PlatformPointerToControl(el, e);
            if (_drawable.PixelToPrice(pc.Y) is decimal cpr && cpr > 0m)
            {
                _drawable.BuildingPolylineCursor = new DrawPoint(_drawable.PixelToTime(pc.X), cpr);
                Chart.Invalidate();
            }
        }

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

                // Y: drag down (dy > 0) = both bounds rise = candles move down with the cursor —
                // but only in manual mode. While auto-fitting, the pan stays purely horizontal so
                // the vertical drag doesn't kick the chart out of autofit (Y keeps fitting live).
                if (!_vm.IsYAutoFit)
                {
                    double newMin = _freePanStartYMin + dy * _freePanPricePerPixel;
                    double newMax = _freePanStartYMax + dy * _freePanPricePerPixel;
                    if (newMax > newMin)
                        _vm.SetManualYRange((decimal)newMin, (decimal)newMax);
                }

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
            // Clear-on-release: the ruler vanishes when the drag ends. If the Measure TOOL armed it (vs a
            // Shift-drag), disarm the tool too so it's one-shot like TradingView — pick, drag, gone.
            _drawable.Measure = default;
            if (_vm != null && _vm.DrawTool == DrawTool.Measure) _vm.DrawTool = DrawTool.None;
            Chart.Invalidate();
        }

        if (_dragMode == DragMode.Drawing)
        {
            // A tool-placed trendline the user clicked without dragging is a degenerate dot — drop
            // it rather than leave an invisible zero-length line on the chart.
            if (_drawDragIsNew && !_drawDragMoved && _draggingDrawingId is Guid nid)
                _vm?.RemoveDrawing(nid);
            // A NEW drawing that was dragged out & kept → revert to cursor + select it (settings pop up).
            else if (_drawDragIsNew && _drawDragMoved && _draggingDrawingId is Guid kid)
                FinishPlacement(kid);
            // Record a Move on the undo stack for an EXISTING drawing that actually moved (a brand-new
            // drawing is already covered by its Add entry, so undo removes the whole creation).
            else if (!_drawDragIsNew && _drawDragMoved)
                _vm?.RecordDrawingMoved(_drawDragOrig);
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

    private void OnPlatformRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (_vm == null || sender is not Microsoft.UI.Xaml.UIElement el) return;
        var pos = e.GetPosition(el);
        var p = new PointF((float)pos.X, (float)pos.Y);

        // Priority 1: right-click is the universal "stop drawing". Abort an in-progress polyline or a
        // mid-drag draw (dropping a brand-new stray line), then disarm the tool back to pan mode.
        if (_polyBuilding)
        {
            CancelPolyline();
            _vm.DrawTool = DrawTool.None;
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.Drawing)
        {
            if (_drawDragIsNew && _draggingDrawingId is Guid nid) _vm.RemoveDrawing(nid);
            _draggingDrawingId = null;
            _drawable.DraggingDrawingId = null;
            _dragMode = DragMode.None;
            _vm.DrawTool = DrawTool.None;
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 1.5: a drawing is selected — right-click just DESELECTS (clears the style-bar +
        // any multi-selection) instead of removing it. A follow-up right-click then disarms the tool.
        if (_vm.SelectedDrawingId is not null || _vm.SelectedDrawingIds.Count > 0)
        {
            _vm.ClearDrawingSelection();
            Chart.Invalidate();
            e.Handled = true;
            return;
        }

        // Priority 2: right-click on an existing drawing removes it.
        if (_drawable.HitDrawing(p) is { } dh)
        {
            _vm.RemoveDrawing(dh.Drawing.Id);
            e.Handled = true;
            return;
        }

        // Priority 3: empty chart — right-click disarms the active tool (back to pan mode) and clears
        // any selection.
        _vm.DrawTool = DrawTool.None;
        _vm.ClearDrawingSelection();
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

        // Ctrl+Z = undo, Ctrl+Shift+Z / Ctrl+Y = redo (drawing add/delete/move history).
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
                       .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                       & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ctrl && e.Key == Windows.System.VirtualKey.Z)
        {
            var cmd = shift ? _vm.RedoCommand : _vm.UndoCommand;
            if (cmd.CanExecute(null)) cmd.Execute(null);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Windows.System.VirtualKey.Y)
        {
            if (_vm.RedoCommand.CanExecute(null)) _vm.RedoCommand.Execute(null);
            e.Handled = true;
            return;
        }

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
            case Windows.System.VirtualKey.Escape:
                // Escape abandons an in-progress polyline build (mirrors right-click cancel).
                if (_polyBuilding) { CancelPolyline(); e.Handled = true; }
                break;
            case Windows.System.VirtualKey.Delete:
            case Windows.System.VirtualKey.Back:
                // Delete / Backspace removes ALL selected drawings (single or shift-multi); each undoable.
                if (_vm.SelectedDrawingId is not null || _vm.SelectedDrawingIds.Count > 0)
                {
                    _vm.RemoveSelectedDrawings();
                    e.Handled = true;
                }
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
