using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// The chart's DRAWING view-model: user drawings, selection, undo/redo, persistence, the pen/tool tray,
// tool groups, and the colour pickers. Split out of ChartViewModel so the rail + pen panel bind a focused
// VM while the canvas keeps ChartViewModel. Plain ObservableObject (NOT StockAwareViewModel) so it doesn't
// double-subscribe the stock pipeline — the parent owns that and drives LoadFor on a stock switch.
// Two seams into the canvas VM, injected via Attach: the live price (for "+ Line at current price") and the
// coalesced redraw request (kept on the parent so its 16ms coalescer stays the single instance).
public partial class ChartDrawingViewModel : ObservableObject
{
    private readonly ILogger<ChartDrawingViewModel> _logger;
    private readonly IDrawingStore _store;

    private Func<decimal?>? _currentPrice;
    private Action? _requestRedraw;

    public ChartDrawingViewModel(ILogger<ChartDrawingViewModel> logger, IDrawingStore store)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        Drawings.CollectionChanged += (_, __) => RequestRedraw();

        // Stamp the shared pen-style commands onto each tile (mirrors MaConfig.RemoveCommand) so the
        // DataTemplate binds Command directly + passes the tile's own value as the parameter.
        foreach (var t in PenColorTiles)  t.Command = PickStrokeColorCommand;   // auto-apply + close
        foreach (var t in PenWidthTiles)  t.Command = SetDefaultThicknessCommand;
        foreach (var t in PenDashTiles)   t.Command = SetDefaultDashCommand;
        foreach (var t in PenEndingTiles) t.Command = SetDefaultEndingCommand;
        foreach (var t in PenHeadTiles)   t.Command = SetDefaultHeadCommand;
        foreach (var t in PenFillTiles)   t.Command = PickFillColorCommand;     // auto-apply + close

        // Seed the pen-tray specimens + selection flags from the loaded default pen.
        RefreshPenTiles();
    }

    // Wire the two canvas seams once, from ChartViewModel's constructor.
    public void Attach(Func<decimal?> currentPrice, Action requestRedraw)
    {
        _currentPrice = currentPrice;
        _requestRedraw = requestRedraw;
    }

    // Route every redraw through the parent's coalescer (the single 16ms/Interlocked instance).
    private void RequestRedraw() => _requestRedraw?.Invoke();

    // --- Drawings collection + selection ------------------------------------------------------------
    // User drawings for the selected stock, anchored in (time, price) so they survive pan/zoom.
    // Persisted per stock+currency (see PersistDrawings); reloaded on stock change via LoadFor.
    public ObservableCollection<DrawingObject> Drawings { get; } = new();

    // The currently selected drawing (tap-to-select). Drives the floating style-bar's visibility
    // and the drawable's grab-handle emphasis; a tap on empty chart clears it.
    [ObservableProperty] private Guid? _selectedDrawingId;

    // Full multi-selection set (shift-click adds/toggles; plain click replaces). SelectedDrawingId is the
    // PRIMARY (drives the style panel); Delete removes ALL of these. The drawable highlights every member.
    public ObservableCollection<Guid> SelectedDrawingIds { get; } = new();

    public bool HasSelectedDrawing => SelectedDrawingId is not null;
    // The pen panel is unified: no selection edits the saved default pen; a selection edits that
    // drawing. These drive the panel's mode (TOOL row + "+ Line" vs "Set as default" + "Delete") and header.
    public bool IsDefaultPenMode => !HasSelectedDrawing;
    public string PenPanelHeader => HasSelectedDrawing ? "Selected line" : "Pen";

    // Remembered so a deselect restores the panel to whatever it was before the selection opened it.
    private bool _penPanelWasOpenBeforeSelect;

    partial void OnSelectedDrawingIdChanged(Guid? oldValue, Guid? newValue)
    {
        OnPropertyChanged(nameof(HasSelectedDrawing));
        OnPropertyChanged(nameof(IsDefaultPenMode));
        OnPropertyChanged(nameof(PenPanelHeader));
        // Colour pickers are per-item popups: any selection change closes them so the next open re-seeds
        // fresh from the newly-selected drawing's colour (no stale popup carrying the previous item's state).
        StrokeColorPickerOpen = false;
        FillColorPickerOpen = false;
        // Selecting a drawing ALWAYS shows its settings; deselecting reverts to how the panel was before.
        if (newValue is not null)
        {
            if (oldValue is null) _penPanelWasOpenBeforeSelect = IsPenPanelOpen;
            IsPenPanelOpen = true;
        }
        else if (oldValue is not null && !_penPanelWasOpenBeforeSelect)
        {
            IsPenPanelOpen = false;
        }
        RefreshPenTiles();   // the effective (selected-vs-default) style changed
        RequestRedraw();
    }

    // --- Pen-panel visibility -----------------------------------------------------------------------
    // The mutual-exclusion with the MA settings overlay lives on ChartViewModel, which watches this
    // property via PropertyChanged (it can close the MA panel; this VM can't reach it).
    [ObservableProperty] private bool _isPenPanelOpen;

    [RelayCommand] private void TogglePenPanel() => IsPenPanelOpen = !IsPenPanelOpen;
    [RelayCommand] private void ClosePenPanel() => IsPenPanelOpen = false;

    // Hide/show all drawings (the rail "eye" toggle). Non-destructive: the set is kept + persisted,
    // the drawable just receives an empty array while hidden (see ChartView.UpdateDrawable). Hiding
    // also clears any selection so the style-bar doesn't linger over invisible geometry.
    [ObservableProperty] private bool _drawingsHidden;

    partial void OnDrawingsHiddenChanged(bool value)
    {
        if (value) SelectedDrawingId = null;
        RequestRedraw();
    }

    [RelayCommand]
    private void ToggleDrawingsHidden() => DrawingsHidden = !DrawingsHidden;

    // --- Drawing CRUD -------------------------------------------------------------------------------

    /// <summary>Adds a horizontal-line drawing at the current live price with the default style. The toolbar
    /// "+ Line" entry point; the right-click gesture builds its own at the cursor price. HLine ignores its
    /// T-anchors, so any timestamp works — use now.</summary>
    [RelayCommand]
    private void AddHLineAtCurrent()
    {
        var price = _currentPrice?.Invoke();
        if (price is null || price.Value <= 0m) return;
        var now = TimeHelper.NowUtc();
        AddDrawing(new DrawingObject(
            Guid.NewGuid(), DrawTool.HLine, now, price.Value, now, price.Value, DefaultDrawStyle));
    }

    /// <summary>Adds a drawing and persists the set. Called by ChartView when a tool places one.</summary>
    public void AddDrawing(DrawingObject d)
    {
        Drawings.Add(d);
        PersistDrawings();
        _undoStack.Push(DrawingMutation.Add(d));
        NotifyUndoState();
    }

    /// <summary>Plain click: select exactly this drawing (drops any multi-selection).</summary>
    public void SelectSingle(Guid id)
    {
        SelectedDrawingIds.Clear();
        SelectedDrawingIds.Add(id);
        SelectedDrawingId = id;   // OnChanged opens the panel + closes pickers + refreshes tiles
        RequestRedraw();
    }

    /// <summary>Shift click: toggle this drawing in/out of the multi-selection; primary follows the last touch.</summary>
    public void AddToSelection(Guid id)
    {
        if (SelectedDrawingIds.Contains(id))
        {
            SelectedDrawingIds.Remove(id);
            SelectedDrawingId = SelectedDrawingIds.Count > 0 ? SelectedDrawingIds[^1] : (Guid?)null;
        }
        else
        {
            SelectedDrawingIds.Add(id);
            SelectedDrawingId = id;
        }
        RequestRedraw();
    }

    /// <summary>Clear all selection (tap empty chart / right-click deselect).</summary>
    public void ClearDrawingSelection()
    {
        SelectedDrawingIds.Clear();
        SelectedDrawingId = null;
        RequestRedraw();
    }

    /// <summary>Delete-key: remove EVERY selected drawing (each undoable), then clear the selection.</summary>
    public void RemoveSelectedDrawings()
    {
        var ids = new List<Guid>(SelectedDrawingIds);
        if (SelectedDrawingId is Guid p && !ids.Contains(p)) ids.Add(p);
        SelectedDrawingIds.Clear();
        foreach (var id in ids) RemoveDrawing(id);   // pushes a Delete + persists per drawing
        SelectedDrawingId = null;
    }

    /// <summary>Removes a drawing by id (✕ glyph or right-click) and persists.</summary>
    public void RemoveDrawing(Guid id)
    {
        DrawingObject removed = default;
        bool found = false;
        for (int i = Drawings.Count - 1; i >= 0; i--)
            if (Drawings[i].Id == id) { removed = Drawings[i]; Drawings.RemoveAt(i); found = true; break; }
        SelectedDrawingIds.Remove(id);
        if (SelectedDrawingId == id) SelectedDrawingId = null;
        PersistDrawings();
        if (found) { _undoStack.Push(DrawingMutation.Delete(removed)); NotifyUndoState(); }
    }

    /// <summary>
    /// Replaces a drawing in place during a drag (repositioning an endpoint or the whole shape).
    /// The indexer set raises CollectionChanged → RequestRedraw. Persistence is deferred to
    /// drag-release (ChartView calls PersistDrawings) so a fast drag doesn't hammer Preferences.
    /// </summary>
    public void UpdateDrawing(DrawingObject d)
    {
        for (int i = 0; i < Drawings.Count; i++)
            if (Drawings[i].Id == d.Id) { Drawings[i] = d; return; }
    }

    [RelayCommand]
    private void DeleteSelectedDrawing()
    {
        if (SelectedDrawingId is Guid id) RemoveDrawing(id);
    }

    // --- Per-stock load ------------------------------------------------------------------------------
    // Clear + reload the drawing set for a stock+currency. Called by ChartViewModel.OnStockChangedAsync.
    // The key folds in the currency so USD/EUR price levels don't bleed across each other on the same stock.
    public void LoadFor(int? stockId, CurrencyType currency)
    {
        Drawings.Clear();
        SelectedDrawingIds.Clear();
        _undoStack.Clear();         // undo history belongs to the previous stock — start fresh
        NotifyUndoState();
        SelectedDrawingId = null;   // a stale selection from the previous stock must not linger
        if (stockId is not int sid) { _drawingsKey = null; _loadedKey = null; return; }

        _drawingsKey = $"{DrawingsPrefKeyBase}{sid}_{currency}";
        _loadedKey = (sid, currency);
        // Load through the store (local cache first, then a best-effort server reconcile). Fire-and-
        // forget — the stock-switch path must not block the new stock's render behind HTTP.
        _ = LoadDrawingsAsync(sid, currency);
    }
}
