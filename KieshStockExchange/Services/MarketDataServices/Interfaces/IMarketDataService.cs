using KieshStockExchange.Models;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices.Interfaces;

public interface IMarketDataService
{
    #region Live Quotes Snapshot
    IReadOnlyDictionary<(int stockId, CurrencyType currency), LiveQuote> Quotes { get; }

    event EventHandler<LiveQuote>? QuoteUpdated;
    #endregion

    #region Subscribtions
    IReadOnlyCollection<(int, CurrencyType)> Subscribed { get; }
    Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task Unsubscribe(int stockId, CurrencyType currency, CancellationToken ct = default);
    Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default);
    Task UnsubscribeAllAsync(CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Subscribe with explicit UI vs bot intent. Bot subscribers (forUi:false) keep
    /// the LiveQuote populated and ref-counted, but the tick path skips UI dispatch
    /// and per-book debounce timers. Existing forUi-less overloads default to true.
    /// </summary>
    Task SubscribeAsync(int stockId, CurrencyType currency, bool forUi, CancellationToken ct = default);
    Task Unsubscribe(int stockId, CurrencyType currency, bool forUi, CancellationToken ct = default);
    Task SubscribeAllAsync(CurrencyType currency, bool forUi, CancellationToken ct = default);
    Task UnsubscribeAllAsync(CurrencyType currency, bool forUi, CancellationToken ct = default);
    #endregion

    #region Tick Handling and rebuilding from history
    /// <summary> When a new transaction (tick) arrives, update the corresponding LiveQuote </summary>
    Task OnTick(Transaction tick, CancellationToken ct = default);

    /// <summary>
    /// Batch-publish multiple ticks. Groups by (stockId,currency) so the ring lock is taken once
    /// per book and a single UI dispatch applies all ticks + schedules one QuoteUpdated.
    /// </summary>
    Task OnTicksAsync(IReadOnlyList<Transaction> ticks, CancellationToken ct = default);

    /// <summary> Build the LiveQuote state from historical ticks stored in the database. </summary>
    Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default);
    #endregion

    #region Convenience lookups
    /// <summary> Get the last traded price for the specified stock and currency. </summary>
    Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary> Get stock details by its unique identifier. </summary>
    Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default);

    /// <summary> Get a list of every stock. </summary>
    Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default);
    #endregion
}
