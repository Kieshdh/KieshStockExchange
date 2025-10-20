using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IMarketOrderService
{
    /// <summary>
    /// Attempts to match the incoming order; returns success/failure and any fill transactions.
    /// </summary>
    Task<OrderResult> PlaceAndMatchAsync(Order incoming, 
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Cancels an existing order in the book.
    /// </summary>
    Task<OrderResult> CancelOrderAsync(int orderId, 
        int? asUserId = null, CancellationToken ct = default);

    /// <summary> 
    /// Modifies an existing order in the book.
    /// </summary>
    Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null, decimal? newPrice = null,
        int? asUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all resting orders for the given stock.
    /// </summary>
    Task<OrderBook> GetOrderBookByStockAsync(int stockId, 
        CurrencyType currency, CancellationToken ct = default);

    /// <summary> 
    /// Validates the order book for the given stock and currency.
    /// </summary>
    public Task<(bool ok, string message)> ValidateBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Fixes any issues found in the order book for the given stock and currency.
    /// </summary>
    public Task<BookFixReport> FixBookAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the index for the order book of the given stock and currency.
    /// </summary>
    public Task RebuildBookIndexAsync(int stockId, CurrencyType currency, CancellationToken ct = default);
}
