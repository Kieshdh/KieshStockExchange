using KieshStockExchange.Helpers;
using KieshStockExchange.Models.ChartDrawing.Objects;

namespace KieshStockExchange.Services.OtherServices.Interfaces;

/// <summary>
/// Client-side, in-memory price-crossing alert evaluator — the non-chart half of the Alert tool.
/// Holds the active alerts and fires a Warning notification (via INotificationService) the first
/// time a live quote crosses an armed alert's level, then disarms it (one-shot; Add re-arms).
/// No persistence, no server round-trip: alerts only live while the app is open.
/// </summary>
public interface IPriceAlertService
{
    /// <summary>Arm a new alert for (stockId, currency) at Level per Condition. Returns its Id.</summary>
    Guid Add(int stockId, CurrencyType currency, decimal level,
        AlertCondition condition = AlertCondition.CrossAny, string? note = null);

    /// <summary>Remove (disarm-and-drop) an alert. Returns false if the id wasn't found.</summary>
    bool Remove(Guid id);

    /// <summary>Snapshot of every currently-tracked alert (armed or not), for the chart tool to render.</summary>
    IReadOnlyList<PriceAlert> Snapshot();
}
