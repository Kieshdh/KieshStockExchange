using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

public interface ISettlementEngine
{
    /// <summary> Balance check and order persist — no reservation writes </summary>
    Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default);

    /// <summary> Settle trades in a self-owned tx. Returns fatal error + unhonorable fills. </summary>
    Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesAsync(
        IReadOnlyList<Transaction> trades, Dictionary<int, Order> ordersById,
        CancellationToken ct = default);

    /// <summary> Settle inside the caller's ambient tx. Mutations recorded in scope for rollback. </summary>
    Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        TradeBatchScope scope,
        CancellationToken ct = default);

    /// <summary> Roll back cache mutations recorded in scope. Call from outer tx's catch. </summary>
    void RestoreCacheSnapshots(Dictionary<int, Order> ordersById, TradeBatchScope scope);

    /// <summary> Mark an order as cancelled </summary>
    Task CancelRemainderAsync(Order order, CancellationToken ct = default);

    /// <summary> Update price/quantity on an existing open order </summary>
    Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default);
}
