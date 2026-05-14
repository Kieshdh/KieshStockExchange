using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

public interface ISettlementEngine
{
    /// <summary> Balance check and order persist — no reservation writes </summary>
    Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default);

    /// <summary>
    /// Persist all trades and transfer assets in a single batched DB transaction.
    /// Returns the optional fatal error (DB write fails, missing fund/position rows) and a
    /// list of fills the seller couldn't honor. Rejected fills never touch the cache or DB —
    /// the caller is expected to cancel the offending maker order and roll back the
    /// matcher's per-fill effect on the book.
    /// </summary>
    Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesAsync(
        IReadOnlyList<Transaction> trades, Dictionary<int, Order> ordersById,
        CancellationToken ct = default);

    /// <summary>
    /// Settle a batch of trades inside a caller-owned transaction. Mutates the cached
    /// Fund/Position instances and BuyBudget on TrueMarketBuy orders, recording the previous
    /// values into <paramref name="scope"/> so the caller can roll the cache back if its
    /// outer transaction fails. The scope's PendingNewPositions is shared across multiple
    /// invocations within one root tx so a (user, stock) created in one settle call is
    /// reused by the next instead of duplicated.
    ///
    /// Returns the optional fatal error and a list of rejected fills (seller short of stock).
    /// Rejected fills never touch the cache or DB.
    /// </summary>
    Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        TradeBatchScope scope,
        CancellationToken ct = default);

    /// <summary>
    /// Roll back the in-memory cache mutations recorded by <see cref="SettleTradesNoTxAsync"/>.
    /// Use this from the catch block of an outer transaction whose commit failed.
    /// </summary>
    void RestoreCacheSnapshots(Dictionary<int, Order> ordersById, TradeBatchScope scope);

    /// <summary> Mark an order as cancelled </summary>
    Task CancelRemainderAsync(Order order, CancellationToken ct = default);

    /// <summary> Update price/quantity on an existing open order </summary>
    Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default);
}
