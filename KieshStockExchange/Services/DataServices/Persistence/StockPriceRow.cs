using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("StockPrices")]
public class StockPriceRow
{
    [PrimaryKey, AutoIncrement]
    [Column("PriceId")] public int PriceId { get; set; }

    [Indexed(Name = "IX_StockPrices_Stock_Curr_Time", Order = 1)]
    [Column("StockId")] public int StockId { get; set; }

    [Column("Price")] public decimal Price { get; set; }

    [Indexed(Name = "IX_StockPrices_Stock_Curr_Time", Order = 2)]
    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    [Indexed(Name = "IX_StockPrices_Stock_Curr_Time", Order = 3)]
    [Column("Timestamp")] public DateTime Timestamp { get; set; }
}

public static class StockPriceMapper
{
    public static StockPrice ToDomain(StockPriceRow r) => new()
    {
        PriceId = r.PriceId,
        StockId = r.StockId,
        Price = r.Price,
        Currency = r.Currency,
        Timestamp = r.Timestamp,
    };

    public static StockPriceRow ToRow(StockPrice p) => new()
    {
        PriceId = p.PriceId,
        StockId = p.StockId,
        Price = p.Price,
        Currency = p.Currency,
        Timestamp = p.Timestamp,
    };
}
