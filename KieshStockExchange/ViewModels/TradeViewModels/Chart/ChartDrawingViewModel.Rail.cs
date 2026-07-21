using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models.ChartDrawing.Tools;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// The LEFT DRAWING-TOOL RAIL: the TradingView-style tool GROUPS, each showing its last-picked tool as the
// rail icon with a "›" flyout, plus per-group open/active state and the tool→icon map. Arming routes through
// SelectDrawTool (in the Pen partial, which owns the armed-tool state); the pen STYLE panel + panel-section
// gates live in ChartDrawingViewModel.Pen.cs. Groups (top→bottom): Lines · Shapes · Position · Draw
// (the "drawing" key = the combined Draw group: brush + text tools). Alert lives on the top toolbar.
public partial class ChartDrawingViewModel
{
    // Each group shows its LAST-PICKED tool as the rail icon; the ">" opens a named flyout of the group's
    // tools. Picking one arms it + becomes the group's icon. The group icon itself arms its current tool.
    [ObservableProperty] private DrawTool _linesGroupTool = DrawTool.Trend;
    [ObservableProperty] private DrawTool _shapesGroupTool = DrawTool.Arrow;
    [ObservableProperty] private DrawTool _drawingGroupTool = DrawTool.Freehand;   // "Draw" group = brush + text
    [ObservableProperty] private DrawTool _positionGroupTool = DrawTool.PositionLong;
    [ObservableProperty] private DrawTool _measureGroupTool = DrawTool.Magnifier;   // Measure + Magnifier(+); zoom-out(−) stays a standalone button
    [ObservableProperty] private string? _openToolGroup;   // "lines"|"shapes"|"position"|"drawing"|"measure"|null (one open)

    partial void OnLinesGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(LinesGroupIcon));
    partial void OnShapesGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(ShapesGroupIcon));
    partial void OnDrawingGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(DrawingGroupIcon));
    partial void OnPositionGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(PositionGroupIcon));
    partial void OnMeasureGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(MeasureGroupIcon));
    partial void OnOpenToolGroupChanged(string? value)
    {
        OnPropertyChanged(nameof(IsLinesGroupOpen));
        OnPropertyChanged(nameof(IsShapesGroupOpen));
        OnPropertyChanged(nameof(IsDrawingGroupOpen));
        OnPropertyChanged(nameof(IsPositionGroupOpen));
        OnPropertyChanged(nameof(IsMeasureGroupOpen));
    }

    public string LinesGroupIcon => ToolIcon(LinesGroupTool);
    public string ShapesGroupIcon => ToolIcon(ShapesGroupTool);
    public string DrawingGroupIcon => ToolIcon(DrawingGroupTool);
    public string PositionGroupIcon => ToolIcon(PositionGroupTool);
    public string MeasureGroupIcon => ToolIcon(MeasureGroupTool);
    public bool IsLinesGroupActive => LinesGroupContains(DrawTool);
    public bool IsShapesGroupActive => ShapesGroupContains(DrawTool);
    public bool IsDrawingGroupActive => DrawingGroupContains(DrawTool);
    public bool IsPositionGroupActive => PositionGroupContains(DrawTool);
    public bool IsMeasureGroupActive => MeasureGroupContains(DrawTool);
    public bool IsLinesGroupOpen => OpenToolGroup == "lines";
    public bool IsShapesGroupOpen => OpenToolGroup == "shapes";
    public bool IsDrawingGroupOpen => OpenToolGroup == "drawing";
    public bool IsPositionGroupOpen => OpenToolGroup == "position";
    public bool IsMeasureGroupOpen => OpenToolGroup == "measure";

    // Group membership — the rail's designed groups (2026-07-21 revision). Fib now sits in Position; Arrow in
    // Shapes; the combined Draw group ("drawing" key) holds the brush + text tools. Alert lives on the toolbar.
    private static bool LinesGroupContains(DrawTool t) => t is DrawTool.Trend or DrawTool.Ray
        or DrawTool.ExtendedLine or DrawTool.HLine or DrawTool.HRay or DrawTool.VLine
        or DrawTool.Polyline or DrawTool.Crossline;
    private static bool ShapesGroupContains(DrawTool t) => t is DrawTool.Arrow or DrawTool.Rectangle
        or DrawTool.Ellipse or DrawTool.Circle or DrawTool.Triangle;
    private static bool DrawingGroupContains(DrawTool t) => t is DrawTool.Freehand or DrawTool.Text
        or DrawTool.Comment or DrawTool.PriceLabel;
    private static bool PositionGroupContains(DrawTool t) => t is DrawTool.Position
        or DrawTool.PositionLong or DrawTool.PositionShort or DrawTool.PositionManual or DrawTool.FibRetracement;
    private static bool MeasureGroupContains(DrawTool t) => t is DrawTool.Measure or DrawTool.Magnifier;

    private static string ToolIcon(DrawTool t) => t switch
    {
        DrawTool.Trend => "tool_trend.png",
        DrawTool.Ray => "tool_ray.png",
        DrawTool.ExtendedLine => "tool_extendedline.png",
        DrawTool.HLine => "tool_hline.png",
        DrawTool.HRay => "tool_hray.png",
        DrawTool.VLine => "tool_vline.png",
        DrawTool.Crossline => "tool_hline.png",   // TODO: dedicated tool_crossline.png asset
        DrawTool.Polyline => "tool_polyline.png",
        DrawTool.Alert => "tool_alert.png",
        DrawTool.Text => "tool_text.png",
        DrawTool.Comment => "tool_text.png",   // TODO: dedicated tool_comment.png asset
        DrawTool.PriceLabel => "tool_text.png",   // TODO: dedicated tool_pricelabel.png asset
        DrawTool.FibRetracement => "tool_fib.png",
        DrawTool.Rectangle => "tool_rectangle.png",
        DrawTool.Ellipse => "tool_ellipse.png",
        DrawTool.Circle => "tool_ellipse.png",   // TODO: dedicated tool_circle.png asset
        DrawTool.Triangle => "tool_rectangle.png",   // TODO: dedicated tool_triangle.png asset
        DrawTool.Arrow => "tool_arrow.png",
        DrawTool.Position or DrawTool.PositionLong or DrawTool.PositionShort or DrawTool.PositionManual => "tool_position.png",
        DrawTool.Freehand => "tool_freehand.png",
        DrawTool.Measure => "tool_measure.png",
        DrawTool.Magnifier => "tool_magnifier.png",
        _ => "tool_cursor.png",
    };

    [RelayCommand] private void ToggleToolGroup(string key) => OpenToolGroup = OpenToolGroup == key ? null : key;

    // Pick a tool from a group flyout: it becomes that group's current tool, is armed, and the flyout closes.
    [RelayCommand]
    private void PickGroupTool(DrawTool tool)
    {
        if (LinesGroupContains(tool)) LinesGroupTool = tool;
        else if (ShapesGroupContains(tool)) ShapesGroupTool = tool;
        else if (DrawingGroupContains(tool)) DrawingGroupTool = tool;
        else if (PositionGroupContains(tool)) PositionGroupTool = tool;
        else if (MeasureGroupContains(tool)) MeasureGroupTool = tool;
        SelectDrawTool(tool);
        OpenToolGroup = null;
    }
}
