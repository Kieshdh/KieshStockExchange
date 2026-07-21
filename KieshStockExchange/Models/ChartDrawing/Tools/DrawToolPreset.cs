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
    bool ShowStroke, bool ShowWidth, bool ShowFillColor, bool ShowOpacity, bool ShowDash,
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
        // Open two-anchor segments that "stop": stroke + width + dash + ending/head.
        DrawTool.Trend or DrawTool.Polyline =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowWidth: true, ShowFillColor: false, ShowOpacity: false, ShowDash: true,
                ShowEnding: true, ShowHead: true, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Straight lines with NO directional ending (they don't "stop" at a second anchor): stroke + width +
        // dash only. Ray + HRay run to infinity ONE way and ExtendedLine BOTH ways, so — like H/V lines — none
        // carry a head/arrow at a terminus that isn't there.
        DrawTool.HLine or DrawTool.HRay or DrawTool.Ray or DrawTool.VLine or DrawTool.ExtendedLine
            or DrawTool.Alert or DrawTool.FibRetracement or DrawTool.Crossline =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowWidth: true, ShowFillColor: false, ShowOpacity: false, ShowDash: true,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Filled shapes: border stroke + width + fill colour + fill opacity + dash. Arrow is a filled block-
        // arrow shape (2 anchors = tail/head, fixed aspect), so it takes the same panel sections as Rectangle.
        DrawTool.Rectangle or DrawTool.Ellipse or DrawTool.Arrow
            or DrawTool.RotatedRect or DrawTool.Triangle or DrawTool.Arc =>
            new(ShapeStyle,
                ShowStroke: true, ShowWidth: true, ShowFillColor: true, ShowOpacity: true, ShowDash: true,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Free-drawn path: stroke (colour/width) + dash + an optional ending arrow + its head shape.
        DrawTool.Freehand =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowWidth: true, ShowFillColor: false, ShowOpacity: false, ShowDash: true,
                ShowEnding: true, ShowHead: true, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),

        // Anchored label / callout: colour + font size + content ONLY — no width/dash/ending. Comment is a
        // Text variant (bubble render) sharing the same panel sections.
        DrawTool.Text or DrawTool.Comment =>
            new(DrawStyle.Default,
                ShowStroke: true, ShowWidth: false, ShowFillColor: false, ShowOpacity: false, ShowDash: false,
                ShowEnding: false, ShowHead: false, ShowText: true, ShowPosition: false,
                ShowSize: true, ShowSmoothing: false),

        // Long/short risk-reward box: entry-line stroke + the Position section (numeric legs; no width tiles).
        // The Long/Short/Manual arming tools share the row so the panel shows while arming, not just when selected.
        DrawTool.Position or DrawTool.PositionLong or DrawTool.PositionShort or DrawTool.PositionManual =>
            new(ShapeStyle,
                ShowStroke: true, ShowWidth: false, ShowFillColor: false, ShowOpacity: false, ShowDash: false,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: true,
                ShowSize: false, ShowSmoothing: false),

        // None / Measure / Magnifier and anything else: transient modes with no persistent style panel.
        _ =>
            new(DrawStyle.Default,
                ShowStroke: false, ShowWidth: false, ShowFillColor: false, ShowOpacity: false, ShowDash: false,
                ShowEnding: false, ShowHead: false, ShowText: false, ShowPosition: false,
                ShowSize: false, ShowSmoothing: false),
    };
}
