using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

public interface IOrderExecutionService
{
    Task<OrderResult> PlaceAndMatchAsync(Order incoming, CancellationToken ct = default);

    /// <summary>§3.6 P4: place a bracket — reserve+insert the parent, insert the SL + TP legs as
    /// dormant (Attached) children, register the bracket, then match the parent (legs arm as it
    /// fills). <paramref name="stopLoss"/> null ⇒ a take-profit-only bracket (no protective stop).
    /// The child orders must already have Side/Entry/Stop/Price set; this assigns their
    /// ParentOrderId + Attached status.</summary>
    Task<OrderResult> PlaceBracketAsync(Order parent, Order? stopLoss,
        IReadOnlyList<Order> takeProfits, CancellationToken ct = default);

    /// <summary>§3.6 P2: reserve + persist a stop order in the armed (Pending) state without
    /// matching. The caller registers it with the trigger watcher on success.</summary>
    Task<OrderResult> ArmStopAsync(Order incoming, CancellationToken ct = default);

    /// <summary>§A1a: arm many SELL stop/trailing orders in one engine pass — share pre-reserve
    /// in the accounts cache, then ONE tx bulk-inserting the Pending rows + persisting the
    /// touched Position reservations. No book, no match. Buy-stops are rejected (arm those
    /// per-order via <see cref="ArmStopAsync"/>). Returns one OrderResult per submitted order
    /// in the same order; the caller registers successes with the trigger watcher.</summary>
    Task<IReadOnlyList<OrderResult>> ArmStopBatchAsync(
        IReadOnlyList<Order> orders, CancellationToken ct = default);

    /// <summary>§3.6 P2: promote an armed stop (by id) to its active type and match it. The
    /// arm-time reservation is reused — no re-reserve, no re-insert.</summary>
    Task<OrderResult> PromoteStopAsync(int orderId, CancellationToken ct = default);

    Task<OrderResult> CancelOrderAsync(int orderId, CancellationToken ct = default);
    Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default);

    /// <summary>§3.6 P3: modify an armed (Pending) stop's StopPrice / stop-limit price / quantity
    /// without touching the book. The reservation delta is applied under the user's gate.</summary>
    Task<OrderResult> ModifyStopAsync(int orderId, int? newQuantity = null,
        decimal? newStopPrice = null, decimal? newLimitPrice = null, CancellationToken ct = default);

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
