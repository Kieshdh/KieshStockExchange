using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

public interface IOrderExecutionService
{
    Task<OrderResult> PlaceAndMatchAsync(Order incoming, CancellationToken ct = default);
    Task<OrderResult> CancelOrderAsync(int orderId, CancellationToken ct = default);
    Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default);

    /// <summary>
    /// Places and matches multiple orders in one engine pass:
    /// one DB transaction for all order inserts, one per-book settlement transaction per stock.
    /// Returns one OrderResult per submitted order in the same order.
    /// </summary>
    Task<IReadOnlyList<OrderResult>> PlaceAndMatchBatchAsync(
        IReadOnlyList<Order> orders, CancellationToken ct = default);

    /// <summary>
    /// Cancels multiple resting orders in one engine pass. Loads orders once, groups by book,
    /// removes from each book under its lock, and writes all cancellations under a single
    /// root DB transaction. Returns one OrderResult per submitted id in the same order.
    /// </summary>
    Task<IReadOnlyList<OrderResult>> CancelOrdersBatchAsync(
        IReadOnlyList<int> orderIds, CancellationToken ct = default);
}