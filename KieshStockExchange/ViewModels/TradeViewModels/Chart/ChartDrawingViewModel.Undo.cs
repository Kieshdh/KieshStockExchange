using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

namespace KieshStockExchange.ViewModels.TradeViewModels;

// Undo / redo (LP3). The UndoStack is a pure invertible history of Add/Delete/Move gestures (cap 50).
// Inverses are applied via RAW collection ops (RawAdd/RawRemove/RawReplace) so they never re-push onto the
// stack. Style-panel edits are intentionally out of scope in v1 (only geometry gestures are undoable).
public partial class ChartDrawingViewModel
{
    private readonly UndoStack _undoStack = new();

    public bool CanUndo => _undoStack.CanUndo;
    public bool CanRedo => _undoStack.CanRedo;

    private void NotifyUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // Called by ChartView on a drag-release that MOVED an existing drawing (new drawings are covered by
    // their Add entry). Looks up the post-drag object by id and records the before→after Move if changed.
    public void RecordDrawingMoved(DrawingObject before)
    {
        var after = Drawings.FirstOrDefault(d => d.Id == before.Id);
        if (after.Id == before.Id && !after.Equals(before))
        {
            _undoStack.Push(DrawingMutation.Move(before, after));
            NotifyUndoState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!_undoStack.Undo(out var m)) return;
        switch (m.Kind)
        {
            case DrawingMutationKind.Add:    RawRemove(m.After.Id); break;   // undo an add = remove it
            case DrawingMutationKind.Delete: RawAdd(m.Before);      break;   // undo a delete = restore it
            case DrawingMutationKind.Move:   RawReplace(m.Before);  break;   // undo a move = revert geometry
        }
        AfterUndoRedo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (!_undoStack.Redo(out var m)) return;
        switch (m.Kind)
        {
            case DrawingMutationKind.Add:    RawAdd(m.After);        break;
            case DrawingMutationKind.Delete: RawRemove(m.Before.Id); break;
            case DrawingMutationKind.Move:   RawReplace(m.After);    break;
        }
        AfterUndoRedo();
    }

    private void AfterUndoRedo()
    {
        PersistDrawings();
        RefreshPenTiles();
        RequestRedraw();
        NotifyUndoState();
    }

    private void RawAdd(DrawingObject d) => Drawings.Add(d);
    private void RawRemove(Guid id)
    {
        for (int i = Drawings.Count - 1; i >= 0; i--)
            if (Drawings[i].Id == id) { Drawings.RemoveAt(i); break; }
        if (SelectedDrawingId == id) SelectedDrawingId = null;
    }
    private void RawReplace(DrawingObject d)
    {
        for (int i = 0; i < Drawings.Count; i++)
            if (Drawings[i].Id == d.Id) { Drawings[i] = d; return; }
    }
}
