using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Candles")]
public class CandleRow
{
    [PrimaryKey, AutoIncrement]
    [Column("CandleId")] public int CandleId { get; set; }

    [Indexed(Name = "IX_Candle_Key", Order = 1, Unique = true)]
    [Column("StockId")] public int StockId { get; set; }

    [Indexed(Name = "IX_Candle_Key", Order = 2, Unique = true)]
    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    [Indexed(Name = "IX_Candle_Key", Order = 3, Unique = true)]
    [Column("BucketSeconds")] public int BucketSeconds { get; set; }

    [Indexed(Name = "IX_Candle_Key", Order = 4, Unique = true)]
    [Column("OpenTime")] public DateTime OpenTime { get; set; }

    [Column("Open")] public decimal Open { get; set; }
    [Column("High")] public decimal High { get; set; }
    [Column("Low")] public decimal Low { get; set; }
    [Column("Close")] public decimal Close { get; set; }

    [Column("Volume")] public long Volume { get; set; }
    [Column("TradeCount")] public int TradeCount { get; set; }

    [Column("MinTransactionId")] public int? MinTransactionId { get; set; }
    [Column("MaxTransactionId")] public int? MaxTransactionId { get; set; }

    [Column("MarketMood")] public double? MarketMood { get; set; }
}

public static class CandleMapper
{
    public static Candle ToDomain(CandleRow r)
    {
        var c = new Candle
        {
            StockId = r.StockId,
            Currency = r.Currency,
            BucketSeconds = r.BucketSeconds,
            OpenTime = r.OpenTime,
            Open = r.Open,
            High = r.High,
            Low = r.Low,
            Close = r.Close,
            Volume = r.Volume,
            TradeCount = r.TradeCount,
            MinTransactionId = r.MinTransactionId,
            MaxTransactionId = r.MaxTransactionId,
            MarketMood = r.MarketMood,
        };
        c.CandleId = r.CandleId;
        return c;
    }

    public static CandleRow ToRow(Candle c) => new()
    {
        CandleId = c.CandleId,
        StockId = c.StockId,
        Currency = c.Currency,
        BucketSeconds = c.BucketSeconds,
        OpenTime = c.OpenTime,
        Open = c.Open,
        High = c.High,
        Low = c.Low,
        Close = c.Close,
        Volume = c.Volume,
        TradeCount = c.TradeCount,
        MinTransactionId = c.MinTransactionId,
        MaxTransactionId = c.MaxTransactionId,
        MarketMood = c.MarketMood,
    };
}
