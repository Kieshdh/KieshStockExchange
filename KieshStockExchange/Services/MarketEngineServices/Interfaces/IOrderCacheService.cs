using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

/// <summary>
/// Per-user snapshot of order rows, partitioned into open and closed lists for direct
/// UI binding. Refreshed explicitly by the caller — there is no DB polling here.
/// </summary>
public interface IOrderCacheService
{
    /// <summary> Every order returned by the last refresh, regardless of status. </summary>
    List<Order> AllOrders { get; }

    /// <summary> Orders that are still open (resting on the book or partially filled). </summary>
    IReadOnlyList<Order> OpenOrders { get; }

    /// <summary> Orders that are no longer open (filled, cancelled or rejected). </summary>
    IReadOnlyList<Order> ClosedOrders { get; }

    /// <summary> Raised after a successful refresh so UI bindings re-read the lists. </summary>
    event EventHandler? OrdersChanged;

    /// <summary> Reload all orders for the user and repartition into Open/Closed. </summary>
    Task<bool> RefreshAsync(int userId, CancellationToken ct = default);
}
