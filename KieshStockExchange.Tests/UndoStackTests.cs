using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

namespace KieshStockExchange.Tests;

/// <summary>
/// UP-CORE — UndoStack state-machine rules: one entry per gesture, redo invalidated on a new push,
/// cap-50 eviction, clear-on-stock-switch, and purge-on-lock (a locked drawing's entries drop so
/// Undo skips it). Lock itself is never pushed.
/// </summary>
public sealed class UndoStackTests
{
    private static DrawingObject Obj(Guid? id = null, decimal p1 = 0m)
        => new(id ?? Guid.NewGuid(), DrawTool.Trend, DateTime.UnixEpoch, p1, DateTime.UnixEpoch, 0m, DrawStyle.Default);

    [Fact]
    public void PushThenUndoRedo_RoundTrips()
    {
        var s = new UndoStack();
        var m = DrawingMutation.Add(Obj());
        s.Push(m);

        Assert.True(s.CanUndo);
        Assert.False(s.CanRedo);

        Assert.True(s.Undo(out var u));
        Assert.Equal(m, u);
        Assert.False(s.CanUndo);
        Assert.True(s.CanRedo);

        Assert.True(s.Redo(out var r));
        Assert.Equal(m, r);
        Assert.True(s.CanUndo);
    }

    [Fact]
    public void NewPush_InvalidatesRedo()
    {
        var s = new UndoStack();
        s.Push(DrawingMutation.Add(Obj()));
        s.Undo(out _);
        Assert.True(s.CanRedo);

        s.Push(DrawingMutation.Add(Obj()));   // a fresh action clears the redo branch
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void ExceedingCapacity_EvictsOldest()
    {
        var s = new UndoStack();
        for (int i = 0; i < UndoStack.Capacity + 10; i++)
            s.Push(DrawingMutation.Add(Obj(p1: i)));

        int undone = 0;
        while (s.Undo(out _)) undone++;
        Assert.Equal(UndoStack.Capacity, undone);   // never more than the cap survive
    }

    [Fact]
    public void Clear_WipesBothStacks()
    {
        var s = new UndoStack();
        s.Push(DrawingMutation.Add(Obj()));
        s.Undo(out _);
        s.Clear();

        Assert.False(s.CanUndo);
        Assert.False(s.CanRedo);
        Assert.False(s.Undo(out _));
    }

    [Fact]
    public void Purge_DropsLockedTargetEntries_SoUndoSkipsIt()
    {
        var s = new UndoStack();
        var locked = Guid.NewGuid();
        var other = Guid.NewGuid();
        s.Push(DrawingMutation.Add(Obj(locked)));
        s.Push(DrawingMutation.Move(Obj(other), Obj(other, 5m)));
        s.Push(DrawingMutation.Delete(Obj(locked)));

        s.Purge(locked);   // the drawing was locked → its entries drop

        // Only the 'other' drawing's mutation remains undoable.
        Assert.True(s.Undo(out var u));
        Assert.Equal(other, u.TargetId);
        Assert.False(s.Undo(out _));
    }
}
