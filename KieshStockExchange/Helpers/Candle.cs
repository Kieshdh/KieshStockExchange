
namespace KieshStockExchange.Helpers;

public readonly record struct Candle(
    int StockId, DateTime OpenTimeUtc, TimeSpan Bucket,
    decimal Open, decimal High, decimal Low, decimal Close
);