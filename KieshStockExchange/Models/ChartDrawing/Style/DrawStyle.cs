namespace KieshStockExchange.Models.ChartDrawing.Style;

// Per-drawing styling picked from the pen tray. Colour + thickness + dash + a line-ending + a head
// shape. Persisted with the drawing (Color round-trips as a hex string via ColorJsonConverter). Arrow
// is the LEGACY bool kept only so pre-Ending JSON still deserializes; the load path migrates a set
// Arrow to Ending=End (see LoadDrawingsForSelected) and nothing writes Arrow anymore. Head defaults to
// FilledTriangle so pre-Head JSON keeps the classic look. Default is the calm blue at 1.5 px solid
// with no ending, so a freshly-placed line matches the previous single-colour look.
//
// UP-CORE trailing-default additions. Legacy JSON lacking these props deserializes to default(T)
// (System.Text.Json does NOT honour optional-ctor defaults for absent params); the load path re-applies
// the non-zero ones via DrawingBackCompat.ApplyLegacyTrailingDefaults:
//   Fill        — shape interior colour; null = no fill (a null Fill round-trips as JSON null).
//   FillOpacity — 0..1 alpha applied to Fill when a shape is filled (0.15 = the subtle default tint).
//   Size        — Text/handle sizing bucket (see SizeKind); superseded by FontSize for Text, kept for back-compat.
//   FontSize    — numeric Text/Comment font size in px; 0 = "use the default" (so legacy Text loads unchanged).
public readonly record struct DrawStyle(
    Color Color, float Thickness, DashKind Dash, bool Arrow = false, LineEnding Ending = LineEnding.None,
    ArrowHeadStyle Head = ArrowHeadStyle.FilledTriangle,
    Color? Fill = null, float FillOpacity = 0.15f, SizeKind Size = SizeKind.Medium, int FontSize = 0)
{
    public static readonly DrawStyle Default = new(Color.FromArgb("#4C9AFF"), 1.5f, DashKind.Solid);
}
