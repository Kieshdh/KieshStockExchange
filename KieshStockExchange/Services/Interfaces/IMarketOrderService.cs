using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IMarketOrderService
{
    /// <summary>
    /// Gets the latest market price for the given stock.
    /// </summary>
    Task<decimal> GetMarketPriceAsync(int stockId);

    /// <summary>
    /// Attempts to match the incoming order; returns success/failure and any fill transactions.
    /// </summary>
    Task<OrderResult> MatchOrderAsync(Order incoming);

    /// <summary>
    /// Cancels an existing order in the book.
    /// </summary>
    Task<OrderResult> CancelOrderAsync(int orderId);

    /// <summary>
    /// Retrieves all resting orders for the given stock.
    /// </summary>
    Task<OrderBook> GetOrderBookByStockAsync(int stockId);

    /// <summary>
    /// Retrieves the stock by its ID.
    /// </summary>
    Task<Stock> GetStockByIdAsync(int stockId);

    /// <summary>
    /// Gets a list of all stocks.
    /// </summary>
    Task<List<Stock>> GetAllStocksAsync();
}
