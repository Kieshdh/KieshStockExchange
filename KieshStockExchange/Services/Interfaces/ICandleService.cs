using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Concurrent;

namespace KieshStockExchange.Services;

public interface ICandleService
{
    /// <summary> For a given (StockId, CurrencyType, CandleResolution) key, holds the in-memory CandleAggregator </summary>
    ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleAggregator> Aggregators { get; }

    /// <summary> Interval at which live candles are flushed to the database. Default is 1 second. </summary>
    TimeSpan FlushInterval { get; }

    /// <summary> Subscribe to candle creation for the given stock, currency and resolution. </summary>
    void Subscribe(int stockId, CurrencyType currency, CandleResolution resolution);

    /// <summary> Unsubscribe from candle creation for the given stock, currency and resolution. </summary>
    Task Unsubscribe(int stockId, CurrencyType currency, CandleResolution resolution);

    /// <summary> Subscribe to candle creation for all stocks in the system, for the given resolution. </summary>
    Task SubscribeAll(CurrencyType currency, CandleResolution resolution, CancellationToken ct = default);

    /// <summary> For each stock in the system, subscribe to default resolutions (5m) for candle creation. </summary>
    Task SubscribeAllDefault(CurrencyType currency, CancellationToken ct = default);

    /// <summary> Feed a new executed transaction (tick) into the appropriate CandleAggregator instance. </summary>
    Task OnTransactionTickAsync(Transaction tick, CancellationToken ct = default);

    /// <summary> Stream closed candles as they are created, for the given stock, currency and resolution. </summary>
    IAsyncEnumerable<Candle> StreamClosedCandles(int stockId, CurrencyType currency,
        CandleResolution resolution, CancellationToken ct = default);

    /// <summary> Try to get the current live (in-progress) candle for the given stock, currency and resolution. </summary>
    Candle? TryGetLiveSnapshot(int stockId, CurrencyType currency, CandleResolution resolution);

    /// <summary> Creates a new empty candle aligned to the given timestamp and resolution. </summary>
    Candle NewCandle(int stockId, CurrencyType currency, DateTime timestamp, TimeSpan resolution, decimal? flatPrice = null);

    /// <summary>
    /// Aggregates a list of equal-resolution candles into a single higher-timeframe candle.
    /// All candles must be for the same StockId, CurrencyType, and base ResolutionSeconds.
    /// </summary>
    Candle AggregateCandles(IReadOnlyList<Candle> candles, int targetBucketSeconds, bool requireFullCoverage = true);

    /// <summary> Convenience overload that accepts a TimeSpan for the target resolution. </summary>
    Candle AggregateCandles(IReadOnlyList<Candle> candles, TimeSpan targetResolution, bool requireFullCoverage = true);
}
