using KieshStockExchange.Models.ChartDrawing.Style;

namespace KieshStockExchange.Models.ChartDrawing.Tools;

// UP-CORE: the per-tool default style + which pen-tray panel sections a tool exposes. This table is
// LOAD-BEARING — ChartViewModel's EditingKind/Show* bools key off it to gate panel sections, and a
// later phase arms a tool with Style. It is pure (no VM/MAUI-lifecycle dependency) so it link-compiles
// into the headless test project. Nothing in the current render/arming path consumes it yet, so adding
// it is render-neutral. Reserved tools (RotatedRect/Triangle/Arc/FibRetracement) get harmless rows —
// no UI reaches them this patch.
public readonly record struct DrawToolPreset(
    DrawStyle Style,
    bool ShowStroke, bool ShowFillColor, bool ShowOpacity, bool ShowDash,
    bool ShowEnding, bool ShowHead, bool ShowText, bool ShowPosition,
    bool ShowSize, bool ShowSmoothing);

public static class DrawToolPresets
{
    // A filled shape's default style — the calm-blue stroke plus the subtle 15% blue tint the
    // pen-tray shows for Rectangle/Ellipse and the reserved shape tools.
    private static readonly DrawStyle ShapeStyle =
        DrawStyle.Default with { Fill = DrawStyle.Default.Color, FillOpacity = 0.15f };

    public static DrawToolPreset For(DrawTool tool) => tool switch
    {
        // Open two-anchor segments that "stop": stroke + dash + ending/head.
        DrawTool.Trend or DrawTool.Ray or DrawTool.Polyline =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowFillColor: false, ShowOpacity: false, ShowDash: true,
                ShowEnding: true, ShowHead: true, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Straight lines with NO directional ending (they don't stop): stroke + dash only. ExtendedLine
        // runs to infinity BOTH ways, so — like H/V lines — it carries no head/arrow.
        DrawTool.HLine or DrawTool.HRay or DrawTool.VLine or DrawTool.ExtendedLine
            or DrawTool.Alert or DrawTool.FibRetracement =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowFillColor: false, ShowOpacity: false, ShowDash: true,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Filled shapes: border stroke + fill colour + fill opacity + dash. Arrow is a filled block-arrow
        // shape (2 anchors = tail/head, fixed aspect), so it takes the same panel sections as Rectangle.
        DrawTool.Rectangle or DrawTool.Ellipse or DrawTool.Arrow
            or DrawTool.RotatedRect or DrawTool.Triangle or DrawTool.Arc =>
            new(ShapeStyle,
                ShowStroke: true, ShowFillColor: true, ShowOpacity: true, ShowDash: true,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Free-drawn path: stroke (colour/width) + dash + an optional ending arrow + its head shape.
        DrawTool.Freehand =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowFillColor: false, ShowOpacity: false, ShowDash: true,
                ShowEnding: true, ShowHead: true, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Anchored label: text + colour + size.
        DrawTool.Text =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowFillColor: false, ShowOpacity: false, ShowDash: false,
                ShowEnding: false, ShowHead: false, ShowText: true, ShowPosition: false,
                ShowSize: true, ShowSmoothing: false),

        // Long/short risk-reward box: stroke + the Position section.
        DrawTool.Position =>
            new(ShapeStyle,
                ShowStroke: true, ShowFillColor: false, ShowOpacity: false, ShowDash: false,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: true,
                ShowSize: false, ShowSmoothing: false),

        // None / Measure / Magnifier and anything else: transient modes with no persistent style panel.
        _ =>
            new(DrawStyle.Default,
                ShowStroke: false, ShowFillColor: false, ShowOpacity: false, ShowDash: false,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),
    };
}
