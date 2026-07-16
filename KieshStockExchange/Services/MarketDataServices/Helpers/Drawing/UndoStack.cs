using KieshStockExchange.Models.ChartDrawing.Objects;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// UP-CORE: one mutation of the drawings set, invertible for undo/redo. Move stores both snapshots
// (same Id); Add leaves Before default; Delete leaves After default.
public enum DrawingMutationKind { Add, Move, Delete }

public readonly record struct DrawingMutation(DrawingMutationKind Kind, DrawingObject Before, DrawingObject After)
{
    public static DrawingMutation Add(DrawingObject added) => new(DrawingMutationKind.Add, default, added);
    public static DrawingMutation Delete(DrawingObject removed) => new(DrawingMutationKind.Delete, removed, default);
    public static DrawingMutation Move(DrawingObject before, DrawingObject after) => new(DrawingMutationKind.Move, before, after);

    // The drawing this mutation targets. Delete carries the target in Before; Add/Move in After.
    public Guid TargetId => Kind == DrawingMutationKind.Delete ? Before.Id : After.Id;
}

// Bounded (cap 50) undo/redo of drawing mutations, one entry per gesture. Pure state machine — the VM
// applies the returned mutation against its Drawings collection. Rules (unit-tested):
//   • a new Push invalidates the redo stack;
//   • the undo stack evicts its oldest entry past the cap;
//   • locking a drawing purges its entries (so Undo skips a locked target) — lock is NOT itself a
//     mutation and is never pushed;
//   • Clear wipes both stacks (called on stock switch).
public sealed class UndoStack
{
    public const int Capacity = 50;

    private readonly List<DrawingMutation> _undo = new();
    private readonly List<DrawingMutation> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(DrawingMutation m)
    {
        _redo.Clear();                       // a fresh action invalidates the redo branch
        _undo.Add(m);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);   // evict the oldest
    }

    public bool Undo(out DrawingMutation m)
    {
        if (_undo.Count == 0) { m = default; return false; }
        m = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(m);
        return true;
    }

    public bool Redo(out DrawingMutation m)
    {
        if (_redo.Count == 0) { m = default; return false; }
        m = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(m);
        return true;
    }

    // Drops every entry targeting the given drawing from both stacks — the purge-on-lock rule. After a
    // purge the locked drawing has no undoable history, so Undo naturally skips it.
    public void Purge(Guid drawingId)
    {
        _undo.RemoveAll(x => x.TargetId == drawingId);
        _redo.RemoveAll(x => x.TargetId == drawingId);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
