using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models.ChartDrawing.Objects;

// UP-CORE: an active price-crossing alert (the non-chart half of the Alert tool — the chart
// drawable that lets a user place/drag the alert line is a separate, still-churning refactor).
// Client-side + in-memory only: PriceAlertService holds these, no persistence, no server
// round-trip. One-shot — IsArmed flips false the instant it fires; Add() re-arms a fresh one.
public sealed class PriceAlert
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required int StockId { get; init; }
    public required CurrencyType Currency { get; init; }
    public required decimal Level { get; init; }
    public AlertCondition Condition { get; init; } = AlertCondition.CrossAny;

    /// <summary>Optional user label shown alongside the fired notification.</summary>
    public string? Note { get; init; }

    /// <summary>One-shot latch: true until the alert fires, then false until re-armed via Add().</summary>
    public bool IsArmed { get; set; } = true;
}

// Pure crossing test for a single (last, new) price sample against a level. No chart/service
// dependency — lives here (Models/ChartDrawing/**) so the test project's link-compile picks it
// up and it gets real unit tests without any DI/SignalR plumbing.
public static class AlertCrossing
{
    /// <summary>
    /// True when price moved from one side of <paramref name="level"/> to the other (or onto it)
    /// going from <paramref name="lastPrice"/> to <paramref name="newPrice"/>, per <paramref name="condition"/>.
    /// <para>
    /// Boundary: touching the level counts as completing the cross on the NEW sample —
    /// CrossUp is <c>last &lt; level &lt;= new</c>, CrossDown is <c>last &gt; level &gt;= new</c>. Landing
    /// exactly on the level from one side fires that direction only (CrossAny still fires once,
    /// not twice). Starting exactly on the level (<c>last == level</c>) does not itself fire —
    /// the alert waits for a subsequent move that actually crosses.
    /// </para>
    /// </summary>
    public static bool Crossed(decimal lastPrice, decimal newPrice, decimal level, AlertCondition condition)
    {
        bool up = lastPrice < level && newPrice >= level;
        bool down = lastPrice > level && newPrice <= level;
        return condition switch
        {
            AlertCondition.CrossUp => up,
            AlertCondition.CrossDown => down,
            _ => up || down,
        };
    }
}
