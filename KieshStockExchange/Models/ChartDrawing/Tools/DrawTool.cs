namespace KieshStockExchange.Models.ChartDrawing.Tools;

// Active chart drawing tool (toolbar cycle). None = normal pan/interact; HLine = one-click
// horizontal line at a price; Trend = a click-drag two-anchor line segment; Ray = a click-drag
// segment extended infinitely past its 2nd anchor; HRay = one-click horizontal ray running right
// from the click; Polyline = multi-vertex line (left-click drops each vertex, double-click ends).
// Measure = a transient drag-ruler (drag to read Δ%/Δtime/#bars); it draws nothing persistent and the
// tool disarms itself on release (one-shot, TradingView-style). Magnifier = drag a box to zoom the
// viewport to it (also a transient action, no persistent drawing). VLine = vertical time line; Freehand
// = free-drawn path; Rectangle/Ellipse = 2-corner shapes with fill; Text = anchored label; Position =
// long/short risk-reward box; Alert = a price line that fires a notification; Arrow = a marker pointing
// at a candle. The tool is a transient UI mode; the drawings the shape tools produce are what get
// persisted. (See docs/CHART_DRAWING_OVERHAUL_PLAN.md.)
// NOTE: append-only — DrawingObject.Kind persists by value, so never reorder/insert existing members.
// The rail's display order is set in XAML, independent of these ordinals.
public enum DrawTool
{
    None, HLine, Trend, Ray, HRay, Polyline, Measure,
    VLine, Freehand, Rectangle, Ellipse, Text, Magnifier, Position, Alert, Arrow,
    ExtendedLine,   // a trend line extended to infinity in BOTH directions
    // UP-CORE appends (reserved for post-ship UI; unreachable without UI this patch does not add):
    RotatedRect, Triangle, Arc, FibRetracement,
    Comment,   // Text-group callout: text in a rounded bubble with a downward tail to its anchor
    // Position arming tools — all COMMIT as Kind=Position; Direction is fixed by the chosen tool (not drag).
    // Long/Short = two-anchor drag (entry→target); Manual = one-click drop then edit the legs in the panel.
    PositionLong, PositionShort, PositionManual,
    Crossline,   // Lines group: a combined horizontal (at P1) + vertical (at T1) cross line, one-click
    Circle,      // Shapes group: a perfect circle (2-corner drag; a square-constrained ellipse)
    PriceLabel,  // Draw group: one-click pill showing the PRICE at its (T1,P1) anchor (no typing)
}
