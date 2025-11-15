using KieshStockExchange.Models;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services;

public interface IMarketDataService
{
    // Live quotes -------------------------------------------------------------
    IReadOnlyDictionary<(int stockId, CurrencyType currency), LiveQuote> Quotes { get; }

    event EventHandler<LiveQuote>? QuoteUpdated;

    // Subscribe / Unsubscribe (keeps quotes in memory; ref-counted)
    IReadOnlyCollection<(int, CurrencyType)> Subscribed { get; }
    Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task Unsubscribe(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default);
    Task UnsubscribeAllAsync(CurrencyType currency, CancellationToken ct = default);

    // History bootstrap + candle stream --------------------------------------
    /// <summary> When a new transaction (tick) arrives, update the corresponding LiveQuote </summary>
    Task OnTick(Transaction tick, CancellationToken ct = default);

    /// <summary> Build the LiveQuote state from historical ticks stored in the database. </summary>
    Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary> Change the duration for which ticks are stored in the ring buffer. </summary>
    void ChangeStoreDuration(RingBufferDuration duration);

    // Convenience lookups -----------------------------------------------------
    /// <summary> Get the last traded price for the specified stock and currency. </summary>
    Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary> Get stock details by its unique identifier. </summary>
    Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default);

    /// <summary> Get a list of every stock. </summary>
    Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default);

    // For testing purposes
    void StartRandomDisplayTicker(int stockId, CurrencyType currency);

    void StopRandomDisplayTicker(int stockId, CurrencyType currency);
}

/// <summary> Type-safe enum for ring buffer duration settings. </summary>
public enum RingBufferDuration { OneMinute, FiveMinutes, FifteenMinutes, OneHour }
