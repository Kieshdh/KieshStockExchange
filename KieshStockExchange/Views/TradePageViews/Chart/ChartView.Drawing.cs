using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.Views.TradePageViews;

// Drawing creation / selection helpers for ChartView (partial): begin/drag/finish a drawing +
// the polyline build lifecycle. Invoked from the Windows gesture handlers; kept cross-platform.
public partial class ChartView
{
    // After a tool places a drawing: revert to the cursor tool and auto-select the new drawing so its
    // settings pop up immediately (owner: one-shot tools + edit-the-thing-you-just-drew).
    private void FinishPlacement(Guid id)
    {
        if (_vm == null) return;
        _vm.Drawing.DrawTool = DrawTool.None;
        _vm.Drawing.SelectSingle(id);
        _vm.Drawing.IsPenPanelOpen = true;
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
            // Body: HLine tracks the cursor price; VLine tracks the cursor time; multi-point strokes
            // (polyline/freehand) shift every point by the data-space delta; Trend shifts both anchors.
            _ when d.Kind == DrawTool.HLine => d with { P1 = price },
            _ when d.Kind == DrawTool.VLine => d with { T1 = time },
            _ when d.Kind == DrawTool.Polyline || d.Kind == DrawTool.Freehand
                => ShiftPoints(d, time - _drawDragStartTime, price - _drawDragStartPrice),
            _ => ShiftTrend(d, time - _drawDragStartTime, price - _drawDragStartPrice),
        };
        upd = upd with { Id = id };
        _vm.Drawing.UpdateDrawing(upd);
    }

    private static DrawingObject ShiftTrend(DrawingObject d, TimeSpan dt, decimal dPrice)
        => d with { T1 = d.T1 + dt, P1 = d.P1 + dPrice, T2 = d.T2 + dt, P2 = d.P2 + dPrice };

    // Shift every vertex of a multi-point stroke (polyline/freehand) by the data-space delta, so a
    // body-drag moves the whole stroke rigidly (its geometry lives in Points, not the T1/P1/T2/P2 anchors).
    private static DrawingObject ShiftPoints(DrawingObject d, TimeSpan dt, decimal dPrice)
    {
        if (d.Points is not { Count: > 0 } pts) return d;
        var moved = new List<DrawPoint>(pts.Count);
        foreach (var pt in pts) moved.Add(pt with { T = pt.T + dt, P = pt.P + dPrice });
        return d with { Points = moved };
    }

    // Finish the in-progress polyline: commit the accumulated vertices as one drawing (needs ≥ 2),
    // select it for immediate styling, then clear the building state.
    private void CommitPolyline()
    {
        if (_vm != null && _polyPoints.Count >= 2)
        {
            var id = Guid.NewGuid();
            _vm.Drawing.AddDrawing(new DrawingObject(
                id, DrawTool.Polyline, default, 0m, default, 0m, _vm.Drawing.DefaultDrawStyle, _polyPoints.ToList()));
            CancelPolyline();       // clear the in-progress preview first
            FinishPlacement(id);    // revert to cursor + select the new polyline (settings pop up)
            return;
        }
        CancelPolyline();
    }

    // Abort / clear the in-progress polyline build (called on commit, right-click, Escape, tool or
    // stock switch). Resets the preview state on the drawable so the rubber-band vanishes.
    private void CancelPolyline()
    {
        _polyBuilding = false;
        _polyPoints.Clear();
        _lastPolyClickMs = 0;
        _drawable.BuildingPolyline = null;
        _drawable.BuildingPolylineCursor = null;
        Chart.Invalidate();
    }
}
