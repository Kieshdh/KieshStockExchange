using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Helpers;

/// <summary>
/// Equality on the candle's identity tuple â€” stock, currency, bucket size and open time.
/// Use this when deduplicating candles regardless of OHLC/volume drift between sources.
/// </summary>
public sealed class CandleKeyComparer : IEqualityComparer<Candle>
{
    public static CandleKeyComparer Instance { get; } = new();

    public bool Equals(Candle? x, Candle? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null &&
        x.StockId == y.StockId && x.CurrencyType == y.CurrencyType &&
        x.OpenTime == y.OpenTime && x.BucketSeconds == y.BucketSeconds);

    public int GetHashCode(Candle obj) =>
        HashCode.Combine(obj.StockId, obj.CurrencyType, obj.BucketSeconds, obj.OpenTime);
}

/// <summary>
/// Equality on the candle's full payload â€” identity tuple plus OHLC, volume, trade count
/// and transaction-id range. Use this to detect whether a candle's contents actually
/// changed between two snapshots.
/// </summary>
public sealed class CandleFullComparer : IEqualityComparer<Candle>
{
    public static CandleFullComparer Instance { get; } = new();

    public bool Equals(Candle? x, Candle? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null &&
        x.StockId == y.StockId && x.CurrencyType == y.CurrencyType &&
        x.BucketSeconds == y.BucketSeconds && x.OpenTime == y.OpenTime &&
        x.Open == y.Open && x.High == y.High && x.Low == y.Low && x.Close == y.Close &&
        x.Volume == y.Volume && x.TradeCount == y.TradeCount &&
        x.MinTransactionId == y.MinTransactionId && x.MaxTransactionId == y.MaxTransactionId);

    public int GetHashCode(Candle obj) =>
        CandleKeyComparer.Instance.GetHashCode(obj) ^ HashCode.Combine(
        obj.Open, obj.High, obj.Low, obj.Close, obj.Volume, obj.TradeCount,
        obj.MinTransactionId, obj.MaxTransactionId);
}
