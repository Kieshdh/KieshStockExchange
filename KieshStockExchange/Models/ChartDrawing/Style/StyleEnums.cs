namespace KieshStockExchange.Models.ChartDrawing.Style;

// Line dash pattern for a drawing (the TradingView style-bar Solid/Dash/Dot cycle). Maps to a
// canvas.StrokeDashPattern in the render pass; Solid uses no pattern.
public enum DashKind { Solid, Dash, Dot }

// TradingView-style line endings (the pen tray's ENDING row). None = plain; End = head at the far/
// terminal end pointing outward; BothOut = outward heads at both ends. Start / BothForward remain for
// legacy-JSON back-compat but are no longer offered in the picker. Supersedes the old bool Arrow (== End).
public enum LineEnding { None, End, Start, BothOut, BothForward }

// Head shape drawn wherever a LineEnding places a head. FilledTriangle = the classic solid ▶ (order 0
// so legacy drawings with no persisted head default to it); Open = two barb strokes forming a hollow
// "V"; Outline = the same triangle stroked as an outline with no fill.
public enum ArrowHeadStyle { FilledTriangle, Open, Outline }

// UP-CORE: label / shape sizing bucket surfaced in the pen tray (Text size, handle size, etc.).
// Persists numerically inside DrawStyle; Medium is the trailing-default so legacy JSON reads as Medium.
public enum SizeKind { Small, Medium, Large }
