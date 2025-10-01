using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services;

public interface IChartService
{
    /// <summary>
    /// Build candles from historical data over [startUtc, endUtc).
    /// Buckets are aligned using TimeHelper.FloorToBucketUtc and optionally gap-filled with flat candles.
    /// </summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int stockId,
        CurrencyType currency,
        DateTime startUtc,
        DateTime endUtc,
        TimeSpan bucket,
        bool fillGaps = true,
        CancellationToken ct = default);

    /// <summary>
    /// Convenience overload: use lookback window ending at NowUtc().
    /// </summary>
    Task<IReadOnlyList<Candle>> GetInitialCandlesAsync(
        int stockId,
        CurrencyType currency,
        TimeSpan lookback,
        TimeSpan bucket,
        bool fillGaps = true,
        CancellationToken ct = default);

    /// <summary>
    /// Live stream of candles built from MarketDataService’s ring buffer.
    /// Ensures the ring is backfilled (BuildFromHistoryAsync) and subscribes before streaming.
    /// </summary>
    IAsyncEnumerable<Candle> StreamLiveCandlesAsync(
        int stockId,
        CurrencyType currency,
        TimeSpan bucket,
        bool fillGaps,
        CancellationToken ct = default);

    /// <summary>
    /// Snapshot helper that returns the most recent candle (aligned to 'bucket') using historical data
    /// and, if needed, MarketDataService for the last price fallback.
    /// </summary>
    Task<Candle?> GetLatestCandleAsync(
        int stockId,
        CurrencyType currency,
        TimeSpan bucket,
        CancellationToken ct = default);
}
