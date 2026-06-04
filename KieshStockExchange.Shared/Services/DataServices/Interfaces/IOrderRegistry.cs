using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Interfaces;

/// <summary>
/// Single-canonical-instance registry for <see cref="Order"/> objects keyed by
/// <see cref="Order.OrderId"/>. The book, settlement helpers, and reservation
/// reconciler all resolve through this so any mutation on one ref is visible to
/// every other holder. Pairs with the runtime-only <c>Order.CurrentBuyReservation</c>
/// / <c>Order.CurrentSellReservedQty</c> fields to make reservation lock-step
/// checkable at the order grain.
/// </summary>
public interface IOrderRegistry
{
    /// <summary>Look up the canonical instance for an OrderId.</summary>
    bool TryGet(int orderId, out Order order);

    /// <summary>
    /// Return the canonical instance for <paramref name="candidate"/>'s OrderId.
    /// </summary>
    Order GetOrAdd(Order candidate);

    /// <summary>
    /// Register a freshly-inserted order whose OrderId was just assigned by the DB.
    /// </summary>
    void Register(Order order);

    /// <summary>Remove an order from the registry. No-op if not present.</summary>
    void Remove(int orderId);

    /// <summary>All open buy orders for a user/currency, canonical refs.</summary>
    IReadOnlyList<Order> GetOpenBuysForUser(int userId, CurrencyType ccy);

    /// <summary>All open sell orders for a user/stock, canonical refs.</summary>
    IReadOnlyList<Order> GetOpenSellsForUser(int userId, int stockId);

    /// <summary>
    /// §3.6 P4: armed (Pending) sell-stops for a user/stock, canonical refs. They reserve shares on
    /// the Position at arm time but aren't <c>IsOpen</c>, so the reconciler/clamp must count them
    /// explicitly or their pooled reservation reads as a phantom and gets clamped to zero.
    /// </summary>
    IReadOnlyList<Order> GetArmedSellStopsForUser(int userId, int stockId);

    /// <summary>§3.6 P4: armed (Pending) buy-stops for a user/currency, canonical refs (fund side).</summary>
    IReadOnlyList<Order> GetArmedBuyStopsForUser(int userId, CurrencyType ccy);

    /// <summary>
    /// Snapshot enumeration over all registered orders. Used by the reconciler to name
    /// offending closed orders that still hold a CurrentReservation.
    /// </summary>
    IEnumerable<Order> AllOrders();

    /// <summary>Approximate count, for diagnostics.</summary>
    int Count { get; }

    /// <summary>
    /// Drop every registered order. Call after a DB reseed (e.g. ExcelImportService
    /// reset of the Orders table) so stale Order refs don't survive into the next run.
    /// </summary>
    void Clear();
}
