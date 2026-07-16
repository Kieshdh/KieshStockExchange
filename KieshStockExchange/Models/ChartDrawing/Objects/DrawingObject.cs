using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;

namespace KieshStockExchange.Models.ChartDrawing.Objects;

// One vertex of a Polyline drawing, anchored in DATA space so it survives pan/zoom.
public readonly record struct DrawPoint(DateTime T, decimal P);

// A user drawing anchored in DATA space (time + price) so it survives pan/zoom through the same
// X/Y transforms the candles use. HLine uses only P1 (spans the plot; T-anchors are ignored). A
// Trend/Ray segment runs from (T1,P1) to (T2,P2) — Ray then extends past anchor2 to the plot edge.
// HRay runs right from (T1,P1) at that price. Polyline ignores T1..P2 and connects the Points list.
// Style carries colour/thickness/dash/arrow. Id keys drag/remove/select and JSON-persists per stock.
// Points is null for every non-Polyline kind (trailing/defaulted so legacy JSON still deserializes).
//
// UP-CORE trailing-default additions. Legacy JSON lacking these props deserializes to default(T)
// (System.Text.Json does NOT honour optional-ctor defaults for absent params); the load path re-applies
// the non-zero ones (Smoothing) via DrawingBackCompat.ApplyLegacyTrailingDefaults:
//   Text      — anchored label text (Text tool); null when unused.
//   P3        — third price anchor. For a Position this is the Stop leg.
//   Qty       — quantity (shares). For a Position, the position size.
//   Locked    — protects the drawing from move/edit/delete/undo.
//   Smoothing — Freehand/spline tension (0 = follow points exactly … 1 = very rounded).
//   Direction — EXPLICIT Position long(+1)/short(−1); 0 = not a position. Never inferred from
//               target>entry (that would flip the box mid-type).
// For a Position: P1 = Entry, P2 = Target, P3 = Stop, Qty = shares, Direction = ±1. Risk% is cut
// (no equity hookup in v1); PnL is live-only (computed at render from current price, never stored).
public readonly record struct DrawingObject(
    Guid Id, DrawTool Kind, DateTime T1, decimal P1, DateTime T2, decimal P2, DrawStyle Style,
    IReadOnlyList<DrawPoint>? Points = null,
    string? Text = null, decimal P3 = 0m, decimal Qty = 0m,
    bool Locked = false, float Smoothing = 0.5f, int Direction = 0);
