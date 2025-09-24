using KieshStockExchange.Models;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services;

public interface IMarketDataService
{
    // Live quotes -------------------------------------------------------------
    IReadOnlyDictionary<(int stockId, CurrencyType currency), LiveQuote> Quotes { get; }
    event EventHandler<LiveQuote>? QuoteUpdated;

    // Subscribe / Unsubscribe (keeps quotes in memory; ref-counted)
    Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default);
    void Unsubscribe(int stockId, CurrencyType currency);
    Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default);

    // History bootstrap + candle stream --------------------------------------
    Task OnTick(Transaction tick, CancellationToken ct = default);

    Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    IAsyncEnumerable<Candle> StreamCandlesAsync(int stockId, CurrencyType currency, TimeSpan bucket,
                                                bool fillGaps, CancellationToken ct = default);

    // Convenience lookups -----------------------------------------------------
    Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default);

    Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default);
}

public readonly record struct Candle(
    int StockId, DateTime OpenTimeUtc, TimeSpan Bucket,
    decimal Open, decimal High, decimal Low, decimal Close
);

