using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Concurrent;

namespace KieshStockExchange.Services;

public interface ICandleService
{
    #region Properties
    /// <summary> For a given (StockId, CurrencyType, CandleResolution) key, holds the in-memory CandleAggregator </summary>
    ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleAggregator> Aggregators { get; }

    /// <summary> Interval at which live candles are flushed to the database. Default is 1 second. </summary>
    TimeSpan FlushInterval { get; }

    /// <summary> Currently subscribed (StockId, CurrencyType, CandleResolution) tuples. </summary>
    IReadOnlyCollection<(int, CurrencyType, CandleResolution)> Subscribed { get; }
    #endregion

    #region Subscriptions
    /// <summary> Subscribe to candle creation for the given stock, currency and resolution. </summary>
    void Subscribe(int stockId, CurrencyType currency, CandleResolution resolution);

    /// <summary> Unsubscribe from candle creation for the given stock, currency and resolution. </summary>
    Task UnsubscribeAsync(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct = default);

    /// <summary> Subscribe to candle creation for all stocks in the system, for the given resolution. </summary>
    Task SubscribeAllAsync(CurrencyType currency, CandleResolution resolution, CancellationToken ct = default);

    /// <summary> For each stock in the system, subscribe to default resolutions (5m) for candle creation. </summary>
    Task SubscribeAllDefaultAsync(CurrencyType currency, CancellationToken ct = default);
    #endregion

    #region Candle Operations
    /// <summary> Feed a new executed transaction (tick) into the appropriate CandleAggregator instance. </summary>
    Task OnTransactionTickAsync(Transaction tick, CancellationToken ct = default);

    /// <summary> Stream closed candles as they are created, for the given stock, currency and resolution. </summary>
    IAsyncEnumerable<Candle> StreamClosedCandles(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct);

    /// <summary> Try to get the current live (in-progress) candle for the given stock, currency and resolution. </summary>
    Candle? TryGetLiveSnapshot(int stockId, CurrencyType currency, CandleResolution resolution);

    /// <summary> Get historical candles from the database for the given stock, currency, resolution and time range. </summary>
    Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(int stockId, CurrencyType currency, 
        CandleResolution resolution, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    #endregion

    #region Candle Maintenance
    /// <summary>
    /// Fixes and fills gaps in historical candles for the given stock, currency, resolution and time range.
    /// Returns a report of the actions taken.
    /// </summary>
    Task<CandleFixReport> FixCandlesAsync(int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    #endregion

    #region Candle Creation and Aggregation
    /// <summary> Creates a new empty candle aligned to the given timestamp and resolution. </summary>
    Candle NewCandle(int stockId, CurrencyType currency, DateTime timestamp, TimeSpan resolution, decimal? flatPrice = null);

    /// <summary>
    /// Aggregates a list of equal-resolution candles into a single higher-timeframe candle.
    /// All candles must be for the same StockId, CurrencyType and targetResolution.
    /// </summary>
    Candle AggregateCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution, bool requireFullCoverage = true);

    /// <summary>
    /// Aggregates multiple sets of equal-resolution candles into a list of higher-timeframe candles.
    /// All candles must be for the same StockId, CurrencyType and targetResolution.
    /// </summary>
    List<Candle> AggregateMultipleCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution, 
        bool requireFullCoverage = true, bool allowPartialEdges = true);

    /// <summary> Aggregates and persists candles from a source resolution into a target resolution, for the given stock and currency </summary>
    Task<IReadOnlyList<Candle>> AggregateAndPersistRangeAsync(int stockId, CurrencyType currency, CandleResolution sourceRes,
        CandleResolution targetRes, DateTime fromUtc, DateTime toUtc, bool allowPartialEdges = true, CancellationToken ct = default);
    #endregion
}

public sealed record CandleFixReport(
    int StockId, CurrencyType Currency, CandleResolution Resolution,
    DateTime FromUtc, DateTime ToUtc,
    int MissingCandleCount, int FixedCandleCount,
    int MissedTxCount, int TotalTxCount,
    DateTime? FirstMissing, DateTime? LastMissing
);
