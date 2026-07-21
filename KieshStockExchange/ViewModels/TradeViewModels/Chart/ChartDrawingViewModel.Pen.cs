using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using System.Text.Json;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// The pen tray: the armed tool, the left-rail tool GROUPS, the default pen style, the per-tool panel-section
// gates (Show*), the six specimen tile sets, and the unified style editing (a selection edits that drawing,
// else the saved default pen). RefreshPenTiles is the single place every changing path routes through.
public partial class ChartDrawingViewModel
{
    // Drawing tool (pen-tray TOOL row). A transient UI mode — not persisted; only the drawings it
    // produces are. Always boots to None (we never arm a tool at startup). While a tool is active a
    // chart press places/starts a drawing instead of free-panning (handled in ChartView).
    [ObservableProperty] private DrawTool _drawTool = DrawTool.None;

    // Toolbar pen button text: the word "Line" + the current tool's glyph (the old DrawToolLabel set).
    public string PenToolLabel => DrawTool switch
    {
        DrawTool.HLine    => "Line ─",
        DrawTool.Trend    => "Line ╱",
        DrawTool.Ray      => "Line ↗",
        DrawTool.HRay     => "Line ↦",
        DrawTool.Polyline => "Line /\\↗",
        DrawTool.Measure  => "Measure",
        _                 => "Line",
    };

    partial void OnDrawToolChanged(DrawTool value)
    {
        OnPropertyChanged(nameof(PenToolLabel));
        // Group active-highlight tracks the armed tool; arming anything closes an open group flyout.
        OnPropertyChanged(nameof(IsLinesGroupActive));
        OnPropertyChanged(nameof(IsShapesGroupActive));
        OnPropertyChanged(nameof(IsDrawingGroupActive));
        OnPropertyChanged(nameof(IsPositionGroupActive));
        OnPropertyChanged(nameof(IsTextGroupActive));
        OpenToolGroup = null;
        // With no selection, EditingKind == the armed tool, so arming a tool flips every Show* gate.
        // RefreshPenTiles raises EditingKind + the Show* notifications (nothing else calls it on this path).
        RefreshPenTiles();
    }

    private const string DrawToolLastPrefKey = "chart_draw_tool_last";

    // Pen-tray TOOL tile: arm a tool (or None = cursor). The panel stays open so the user can keep
    // tuning the pen; the pick is remembered for a future pre-highlight but never re-armed at startup.
    [RelayCommand]
    private void SelectDrawTool(DrawTool tool)
    {
        DrawTool = tool;
        Preferences.Default.Set(DrawToolLastPrefKey, tool.ToString());
    }

    // --- Left-rail tool GROUPS (TradingView-style) ----------------------------------------------------
    // Each group shows its LAST-PICKED tool as the rail icon; the ">" opens a named flyout of the group's
    // tools. Picking one arms it + becomes the group's icon. The group icon itself arms its current tool.
    [ObservableProperty] private DrawTool _linesGroupTool = DrawTool.Trend;
    [ObservableProperty] private DrawTool _shapesGroupTool = DrawTool.Rectangle;
    [ObservableProperty] private DrawTool _drawingGroupTool = DrawTool.Freehand;
    [ObservableProperty] private DrawTool _positionGroupTool = DrawTool.Position;
    [ObservableProperty] private DrawTool _textGroupTool = DrawTool.Text;
    [ObservableProperty] private string? _openToolGroup;   // "lines"|"shapes"|"drawing"|"position"|"text"|null (one open)

    partial void OnLinesGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(LinesGroupIcon));
    partial void OnShapesGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(ShapesGroupIcon));
    partial void OnDrawingGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(DrawingGroupIcon));
    partial void OnPositionGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(PositionGroupIcon));
    partial void OnTextGroupToolChanged(DrawTool value) => OnPropertyChanged(nameof(TextGroupIcon));
    partial void OnOpenToolGroupChanged(string? value)
    {
        OnPropertyChanged(nameof(IsLinesGroupOpen));
        OnPropertyChanged(nameof(IsShapesGroupOpen));
        OnPropertyChanged(nameof(IsDrawingGroupOpen));
        OnPropertyChanged(nameof(IsPositionGroupOpen));
        OnPropertyChanged(nameof(IsTextGroupOpen));
    }

    public string LinesGroupIcon => ToolIcon(LinesGroupTool);
    public string ShapesGroupIcon => ToolIcon(ShapesGroupTool);
    public string DrawingGroupIcon => ToolIcon(DrawingGroupTool);
    public string PositionGroupIcon => ToolIcon(PositionGroupTool);
    public string TextGroupIcon => ToolIcon(TextGroupTool);
    public bool IsLinesGroupActive => LinesGroupContains(DrawTool);
    public bool IsShapesGroupActive => ShapesGroupContains(DrawTool);
    public bool IsDrawingGroupActive => DrawingGroupContains(DrawTool);
    public bool IsPositionGroupActive => PositionGroupContains(DrawTool);
    public bool IsTextGroupActive => TextGroupContains(DrawTool);
    public bool IsLinesGroupOpen => OpenToolGroup == "lines";
    public bool IsShapesGroupOpen => OpenToolGroup == "shapes";
    public bool IsDrawingGroupOpen => OpenToolGroup == "drawing";
    public bool IsPositionGroupOpen => OpenToolGroup == "position";
    public bool IsTextGroupOpen => OpenToolGroup == "text";

    // Group membership — the rail's designed groups. Alert lives on the top toolbar (not a rail group);
    // Comment (Text group) + Long/Short/Manual (Position group) join their predicates in later commits.
    private static bool LinesGroupContains(DrawTool t) => t is DrawTool.Trend or DrawTool.Ray
        or DrawTool.ExtendedLine or DrawTool.HLine or DrawTool.HRay or DrawTool.VLine
        or DrawTool.Polyline or DrawTool.FibRetracement;
    private static bool ShapesGroupContains(DrawTool t) => t is DrawTool.Rectangle or DrawTool.Ellipse;
    private static bool DrawingGroupContains(DrawTool t) => t is DrawTool.Freehand or DrawTool.Arrow;
    private static bool PositionGroupContains(DrawTool t) => t is DrawTool.Position;
    private static bool TextGroupContains(DrawTool t) => t is DrawTool.Text;

    private static string ToolIcon(DrawTool t) => t switch
    {
        DrawTool.Trend => "tool_trend.png",
        DrawTool.Ray => "tool_ray.png",
        DrawTool.ExtendedLine => "tool_extendedline.png",
        DrawTool.HLine => "tool_hline.png",
        DrawTool.HRay => "tool_hray.png",
        DrawTool.VLine => "tool_vline.png",
        DrawTool.Polyline => "tool_polyline.png",
        DrawTool.Alert => "tool_alert.png",
        DrawTool.Text => "tool_text.png",
        DrawTool.FibRetracement => "tool_fib.png",
        DrawTool.Rectangle => "tool_rectangle.png",
        DrawTool.Ellipse => "tool_ellipse.png",
        DrawTool.Arrow => "tool_arrow.png",
        DrawTool.Position => "tool_position.png",
        DrawTool.Freehand => "tool_freehand.png",
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
        else if (TextGroupContains(tool)) TextGroupTool = tool;
        SelectDrawTool(tool);
        OpenToolGroup = null;
    }

    // --- Default pen + panel-section gates ----------------------------------------------------------
    // The default pen: the style a freshly-placed drawing gets. Persisted as JSON (Color via the same
    // hex converter as the drawings) so the user's pen survives restarts.
    private const string DefaultDrawStylePrefKey = "chart_draw_style_default";
    [ObservableProperty] private DrawStyle _defaultDrawStyle = LoadDefaultDrawStyle();

    partial void OnDefaultDrawStyleChanged(DrawStyle value)
    {
        try { Preferences.Default.Set(DefaultDrawStylePrefKey, JsonSerializer.Serialize(value, _drawingJson)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Saving default pen style failed."); }
        RefreshPenTiles();
    }

    private static DrawStyle LoadDefaultDrawStyle()
    {
        var json = Preferences.Default.Get(DefaultDrawStylePrefKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return DrawStyle.Default;
        try
        {
            var s = JsonSerializer.Deserialize<DrawStyle>(json, _drawingJson);
            return (s.Color is null || s.Thickness <= 0f) ? DrawStyle.Default : s;
        }
        catch { return DrawStyle.Default; }
    }

    // UP-CORE: the tool whose style panel is showing — the selected drawing's kind, else the armed
    // pen tool. FirstOrDefault (not First) so a momentarily empty selection set returns default
    // (Kind = None) instead of throwing.
    public DrawTool EditingKind =>
        SelectedDrawingId is Guid id ? Drawings.FirstOrDefault(d => d.Id == id).Kind : DrawTool;

    private DrawToolPreset EditingPreset => DrawToolPresets.For(EditingKind);

    // Panel-section gates driven by the preset table. Split ShowFillColor/ShowOpacity so a shape can
    // show both while a line shows neither.
    public bool ShowStroke    => EditingPreset.ShowStroke;
    public bool ShowWidth     => EditingPreset.ShowWidth;   // split from ShowStroke so Text shows colour but no width
    public bool ShowFillColor => EditingPreset.ShowFillColor;
    public bool ShowOpacity   => EditingPreset.ShowOpacity;
    public bool ShowDash      => EditingPreset.ShowDash;
    public bool ShowEnding    => EditingPreset.ShowEnding;
    public bool ShowHead      => EditingPreset.ShowHead;
    public bool ShowText      => EditingPreset.ShowText;
    public bool ShowPosition  => EditingPreset.ShowPosition;
    public bool ShowSize      => EditingPreset.ShowSize;
    public bool ShowSmoothing => EditingPreset.ShowSmoothing;

    // The live specimen of the current pen (or selected line), bound by the toolbar pen button and the
    // panel preview. Rebuilt as a fresh instance on every effective-style change so its hosts repaint.
    [ObservableProperty] private StylePreviewDrawable _penSpecimen = new();

    // The pen-tray palette (10 swatches, 2×5). Order matches the design spec.
    private static readonly Color[] PenPalette =
    {
        Color.FromArgb("#2962FF"), Color.FromArgb("#F23645"), Color.FromArgb("#089981"),
        Color.FromArgb("#FF9800"), Color.FromArgb("#B39DDB"), Color.FromArgb("#4C9AFF"),
        Color.FromArgb("#FFFFFF"), Color.FromArgb("#FFD54F"), Color.FromArgb("#26C6DA"),
        Color.FromArgb("#9E9E9E"),
    };

    // Fixed tile sets — only each tile's Specimen + IsSelected mutate (see RefreshPenTiles).
    public IReadOnlyList<PenColorTile> PenColorTiles { get; } =
        PenPalette.Select(c => new PenColorTile(c)).ToList();
    // Three widths with a clear spread — a hair thin / default / clearly thick.
    public IReadOnlyList<PenWidthTile> PenWidthTiles { get; } =
        new[] { 0.75, 1.5, 3.5 }.Select(w => new PenWidthTile(w)).ToList();
    public IReadOnlyList<PenDashTile> PenDashTiles { get; } =
        new[] { DashKind.Solid, DashKind.Dash, DashKind.Dot }.Select(d => new PenDashTile(d)).ToList();
    // Owner-trimmed to three: none / end / both-out (Start + BothForward stay in the enum for back-compat).
    public IReadOnlyList<PenEndingTile> PenEndingTiles { get; } =
        new[] { LineEnding.None, LineEnding.End, LineEnding.BothOut }
            .Select(e => new PenEndingTile(e)).ToList();
    public IReadOnlyList<PenHeadTile> PenHeadTiles { get; } =
        new[] { ArrowHeadStyle.FilledTriangle, ArrowHeadStyle.Outline, ArrowHeadStyle.Open }
            .Select(h => new PenHeadTile(h)).ToList();
    // FILL swatches (shapes only) — same palette, own selection state + SetPenFill command.
    public IReadOnlyList<PenColorTile> PenFillTiles { get; } =
        PenPalette.Select(c => new PenColorTile(c)).ToList();

    // --- Unified style editing (default pen when nothing selected, else the selection) ---

    // Applies a style transform to the selected drawing in place, then persists + repaints.
    private void MutateSelectedStyle(Func<DrawStyle, DrawStyle> transform)
    {
        if (SelectedDrawingId is not Guid id) return;
        for (int i = 0; i < Drawings.Count; i++)
        {
            if (Drawings[i].Id != id) continue;
            Drawings[i] = Drawings[i] with { Style = transform(Drawings[i].Style) };
            PersistDrawings();
            RefreshPenTiles();   // the selected style is the effective style
            RequestRedraw();
            return;
        }
    }

    // UP-CORE: the single seam LP2/LP3/LP5 route every non-style edit of the selected drawing through
    // (text, fill, size, position legs, smoothing, direction). Mirrors MutateSelectedStyle but takes a
    // whole-object transform and respects Locked (a no-op on a locked target). Persists immediately.
    public void MutateSelectedDrawing(Func<DrawingObject, DrawingObject> mutate)
    {
        if (SelectedDrawingId is not Guid id) return;
        for (int i = 0; i < Drawings.Count; i++)
        {
            if (Drawings[i].Id != id) continue;
            if (Drawings[i].Locked) return;   // locked drawings are protected from edits
            Drawings[i] = mutate(Drawings[i]);
            PersistDrawings();
            RefreshPenTiles();
            RequestRedraw();
            return;
        }
    }

    // UP-CORE: setters for the new per-drawing editable fields, each routing through MutateSelectedDrawing
    // (discrete panel commits → persist now). Continuous anchor drags stay on the UpdateDrawing path.
    public void SetSelectedFill(Color? fill) => MutateSelectedDrawing(d => d with { Style = d.Style with { Fill = fill } });
    public void SetSelectedFillOpacity(float opacity)
        => MutateSelectedDrawing(d => d with { Style = d.Style with { FillOpacity = Math.Clamp(opacity, 0f, 1f) } });
    public void SetSelectedSize(SizeKind size) => MutateSelectedDrawing(d => d with { Style = d.Style with { Size = size } });
    public void SetSelectedText(string? text) => MutateSelectedDrawing(d => d with { Text = text });
    public void SetSelectedQty(decimal qty) => MutateSelectedDrawing(d => d with { Qty = qty });
    public void SetSelectedDirection(int direction) => MutateSelectedDrawing(d => d with { Direction = direction });
    public void SetSelectedSmoothing(float smoothing)
        => MutateSelectedDrawing(d => d with { Smoothing = Math.Clamp(smoothing, 0f, 1f) });
    public void SetSelectedEntryPrice(decimal price) => MutateSelectedDrawing(d => d with { P1 = price });
    public void SetSelectedTargetPrice(decimal price) => MutateSelectedDrawing(d => d with { P2 = price });
    public void SetSelectedStopPrice(decimal price) => MutateSelectedDrawing(d => d with { P3 = price });

    // Each setter edits the SELECTED drawing when there is one, else the saved default pen. The
    // default write routes through the DefaultDrawStyle setter (persist + tile refresh); the selected
    // write through MutateSelectedStyle.
    private void ApplyPenStyle(Func<DrawStyle, DrawStyle> transform)
    {
        if (HasSelectedDrawing) MutateSelectedStyle(transform);
        else DefaultDrawStyle = transform(DefaultDrawStyle);
    }

    [RelayCommand]
    private void SetDefaultColor(Color color)
    {
        if (color is null) return;
        ApplyPenStyle(s => s with { Color = color });
    }

    [RelayCommand]
    private void SetDefaultThickness(double px)
    {
        if (px <= 0) return;
        ApplyPenStyle(s => s with { Thickness = (float)px });
    }

    [RelayCommand]
    private void SetDefaultDash(DashKind dash)
        => ApplyPenStyle(s => s with { Dash = dash });

    [RelayCommand]
    private void SetDefaultEnding(LineEnding ending)
        => ApplyPenStyle(s => s with { Ending = ending });

    // The head-shape tiles only matter when the line carries an ending to hang a head on. The panel greys
    // + disables them (and this command no-ops) whenever the effective ending is None.
    public bool CanEditHead => EffectivePenStyle().Ending != LineEnding.None;

    [RelayCommand]
    private void SetDefaultHead(ArrowHeadStyle head)
    {
        if (!CanEditHead) return;   // no ending ⇒ no head to shape
        ApplyPenStyle(s => s with { Head = head });
    }

    // --- Text font size (standard sizes + ▲/▼ steppers) ---------------------------------------------
    // The universal standard sizes the Text SIZE dropdown offers; steppers snap to these.
    public static readonly int[] StandardFontSizes =
        { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 };
    private const int DefaultFontSize = 12;   // Style.FontSize == 0 (unset/legacy) resolves to this

    // Bound by the SIZE dropdown. Edits the selected Text else the default pen; re-synced from the effective
    // style in RefreshPenTiles (guarded by _syncingPenFromStyle so the push-back doesn't re-enter the setter).
    [ObservableProperty] private int _penFontSize = DefaultFontSize;

    partial void OnPenFontSizeChanged(int value)
    {
        if (_syncingPenFromStyle || value <= 0) return;
        ApplyPenStyle(s => s with { FontSize = value });
    }

    [RelayCommand] private void StepFontSizeUp() => StepFontSize(+1);
    [RelayCommand] private void StepFontSizeDown() => StepFontSize(-1);

    // Snap the current size to the nearest standard size, then move one standard step in dir.
    private void StepFontSize(int dir)
    {
        int cur = PenFontSize > 0 ? PenFontSize : DefaultFontSize;
        int idx = Array.IndexOf(StandardFontSizes, cur);
        if (idx < 0)   // not exactly a standard size ⇒ snap to the nearest
        {
            idx = 0;
            for (int i = 1; i < StandardFontSizes.Length; i++)
                if (Math.Abs(StandardFontSizes[i] - cur) < Math.Abs(StandardFontSizes[idx] - cur)) idx = i;
        }
        PenFontSize = StandardFontSizes[Math.Clamp(idx + dir, 0, StandardFontSizes.Length - 1)];
    }

    // "Set as default ✓": copy the selected line's style to the default pen.
    [RelayCommand]
    private void SetSelectedAsDefault()
    {
        if (SelectedDrawingId is not Guid id) return;
        for (int i = 0; i < Drawings.Count; i++)
            if (Drawings[i].Id == id) { DefaultDrawStyle = NormalizeStyle(Drawings[i].Style); return; }
    }

    // The effective style the pen tray edits/previews: the selected drawing's, else the default pen.
    private DrawStyle EffectivePenStyle()
    {
        if (SelectedDrawingId is Guid id)
            for (int i = 0; i < Drawings.Count; i++)
                if (Drawings[i].Id == id) return NormalizeStyle(Drawings[i].Style);
        return DefaultDrawStyle;
    }

    // Legacy/blank drawings persisted without a colour fall back to the default pen colour + thickness.
    private static DrawStyle NormalizeStyle(DrawStyle s)
        => (s.Color is null || s.Thickness <= 0f)
            ? DrawStyle.Default with { Dash = s.Dash, Ending = s.Ending }
            : s;

    private static StylePreviewDrawable MakeSpecimen(Color c, float th, DashKind dash, LineEnding end,
        ArrowHeadStyle head, StylePreviewMode mode = StylePreviewMode.Line)
        => new() { Color = c, Thickness = th, Dash = dash, Ending = end, Head = head, Mode = mode };

    // Rebuild every pen-tray specimen + selection flag from the effective style. Fresh specimen
    // instances make each hosting GraphicsView repaint through its Drawable binding.
    private void RefreshPenTiles()
    {
        OnPropertyChanged(nameof(CanEditHead));   // ending may have changed ⇒ head-row enablement
        // UP-CORE: EditingKind + the panel-section gates are pure functions of the current
        // selection/armed-tool, so re-raise them here — the one place every changing path routes through.
        OnPropertyChanged(nameof(EditingKind));
        OnPropertyChanged(nameof(ShowStroke));
        OnPropertyChanged(nameof(ShowWidth));
        OnPropertyChanged(nameof(ShowFillColor));
        OnPropertyChanged(nameof(ShowOpacity));
        OnPropertyChanged(nameof(ShowDash));
        OnPropertyChanged(nameof(ShowEnding));
        OnPropertyChanged(nameof(ShowHead));
        OnPropertyChanged(nameof(ShowText));
        OnPropertyChanged(nameof(ShowPosition));
        OnPropertyChanged(nameof(ShowSize));
        OnPropertyChanged(nameof(ShowSmoothing));
        // Colour-picker swatches + fill state track the effective style too.
        OnPropertyChanged(nameof(CurrentStrokeColor));
        OnPropertyChanged(nameof(CurrentFillColor));
        OnPropertyChanged(nameof(HasFill));
        var s = EffectivePenStyle();
        var col = s.Color ?? DrawStyle.Default.Color;
        float th = s.Thickness > 0f ? s.Thickness : DrawStyle.Default.Thickness;
        string colHex = col.ToArgbHex(true);

        PenSpecimen = MakeSpecimen(col, th, s.Dash, s.Ending, s.Head);
        foreach (var t in PenColorTiles) t.IsSelected = t.Color.ToArgbHex(true) == colHex;
        foreach (var t in PenWidthTiles)
        {
            t.Specimen = MakeSpecimen(col, (float)t.Thickness, DashKind.Solid, LineEnding.None, s.Head,
                StylePreviewMode.Dot);
            t.IsSelected = Math.Abs(t.Thickness - th) < 0.01;
        }
        foreach (var t in PenDashTiles)
        {
            t.Specimen = MakeSpecimen(col, th, t.Dash, LineEnding.None, s.Head, StylePreviewMode.Dash);
            t.IsSelected = t.Dash == s.Dash;
        }
        foreach (var t in PenEndingTiles)
        {
            t.Specimen = MakeSpecimen(col, th, s.Dash, t.Ending, s.Head);
            t.IsSelected = t.Ending == s.Ending;
        }
        foreach (var t in PenHeadTiles)
        {
            // Always show a head so the shape reads (use End even if the pen's ending is None).
            t.Specimen = MakeSpecimen(col, th, s.Dash, LineEnding.End, t.Head);
            t.IsSelected = t.Head == s.Head;
        }
        // FILL swatch selection + the opacity slider (guarded so pushing the effective value back into
        // the slider doesn't re-enter the setter).
        string fillHex = (s.Fill ?? Colors.Transparent).ToArgbHex(true);
        foreach (var t in PenFillTiles)
            t.IsSelected = s.Fill is not null && t.Color.ToArgbHex(true) == fillHex;
        _syncingPenFromStyle = true;
        PenFontSize = s.FontSize > 0 ? s.FontSize : DefaultFontSize;
        PenFillOpacity = Math.Clamp(s.FillOpacity, 0f, 1f);
        _syncingPenFromStyle = false;
    }
}
