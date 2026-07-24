using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models.ChartDrawing.Tools;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// INLINE text-editing state for the Text / Comment tools (partial). Replaces the old modal DisplayPrompt:
// the view drops an anchor, then binds a transparent on-chart Entry overlay to InlineEditText and positions
// it at InlineEditX/Y while InlineEditActive. Commit (Enter / blur) writes the label back onto the target
// drawing — or DELETES it when left empty (council rule: auto-delete ONLY on an explicit commit, never
// mid-edit). The view raises the focus via InlineEditRequested; all Drawings mutation stays here in the VM.
public partial class ChartDrawingViewModel
{
    [ObservableProperty] private bool _inlineEditActive;
    [ObservableProperty] private string _inlineEditText = string.Empty;
    [ObservableProperty] private double _inlineEditX;   // overlay Entry TranslationX (chart-pixel anchor)
    [ObservableProperty] private double _inlineEditY;   // overlay Entry TranslationY (chart-pixel anchor)

    private Guid? _inlineEditId;   // the freshly-placed Text/Comment being typed into

    // Raised when an inline edit opens so the view can focus the overlay Entry (VM can't touch controls).
    public event Action? InlineEditRequested;

    // Open the inline editor over a just-placed Text/Comment drawing. x/y are chart pixels (the overlay's
    // grid cell shares the chart's origin, so the click point positions the Entry directly).
    public void StartInlineEdit(Guid id, double x, double y, string seed = "")
    {
        _inlineEditId = id;
        InlineEditText = seed;
        InlineEditX = x;
        InlineEditY = y;
        InlineEditActive = true;
        InlineEditRequested?.Invoke();
    }

    // Commit the typed label. Empty/blank text removes the placeholder (auto-delete on commit); a real label
    // writes back + persists. Idempotent — Enter fires Completed and blur fires Unfocused, both route here.
    [RelayCommand]
    private void CommitInlineEdit()
    {
        if (!InlineEditActive || _inlineEditId is not Guid id) return;
        InlineEditActive = false;
        DrawTool = DrawTool.None;       // one-shot: revert to cursor after committing the label
        string text = (InlineEditText ?? string.Empty).Trim();
        var d = Drawings.FirstOrDefault(x => x.Id == id);
        if (text.Length == 0)
        {
            RemoveDrawing(id);          // never leave an invisible empty label behind
        }
        else if (d.Id == id)
        {
            UpdateDrawing(d with { Text = text });
            PersistDrawings();          // UpdateDrawing defers persistence — commit the label now
            SelectSingle(id);           // select so the style panel opens (matches FinishPlacement)
            IsPenPanelOpen = true;
        }
        _inlineEditId = null;
        InlineEditText = string.Empty;
    }

    // Abandon the edit (Escape). The target is always a fresh placeholder here, so drop it outright.
    [RelayCommand]
    private void CancelInlineEdit()
    {
        if (!InlineEditActive) return;
        InlineEditActive = false;
        DrawTool = DrawTool.None;       // one-shot: revert to cursor on abandon too
        if (_inlineEditId is Guid id) RemoveDrawing(id);
        _inlineEditId = null;
        InlineEditText = string.Empty;
    }
}
