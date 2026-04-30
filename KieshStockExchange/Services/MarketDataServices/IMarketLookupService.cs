using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Pure read-side lookup surface for market data: stock catalog reads, last/at-time
/// price resolution, and historical-tick loading. No live-quote registry, no UI
/// dispatcher — these helpers only touch the in-memory stock catalog, the candle
/// service, and the database, so they can be reused by lookup-only consumers
/// (admin VMs, snapshot service, decision service) without dragging in the
/// subscription pipeline.
/// </summary>
public interface IMarketLookupService
{
    /// <summary>Get stock details by its unique identifier.</summary>
    Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default);

    /// <summary>Get a list of every stock.</summary>
    Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolve the latest price for a book using the candle live snapshot, then the
    /// latest persisted transaction, then the latest persisted stock-price row, and
    /// finally a USD-converted fallback. Returns <c>null</c> when no price source
    /// has any data — callers decide how to handle the empty case.
    /// </summary>
    Task<decimal?> GetLatestPriceFromStoreAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Resolve the price at-or-before the supplied UTC timestamp. Falls back to
    /// the latest persisted stock-price row, then a USD conversion. Returns the
    /// final seed price (100) if nothing else is available.
    /// </summary>
    Task<decimal> GetDateTimePriceAsync(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default);

    /// <summary>
    /// Load today's transactions for a book, sorted ascending by timestamp.
    /// Used by <see cref="IMarketDataService.BuildFromHistoryAsync"/> to seed the
    /// LiveQuote OHLC/volume on first subscribe.
    /// </summary>
    Task<List<Transaction>> LoadHistoricalTicksAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Determine a (price, time) pair to seed an empty LiveQuote when no historical
    /// ticks exist: latest transaction → latest stock-price row → USD-converted →
    /// 100 seed.
    /// </summary>
    Task<(decimal Price, DateTime TimeUtc)> GetFallbackPriceAndTimeAsync(int stockId, CurrencyType currency, CancellationToken ct = default);
}
