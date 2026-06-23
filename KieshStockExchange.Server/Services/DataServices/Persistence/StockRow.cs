using SQLite;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Stocks")]
public class StockRow
{
    [PrimaryKey, AutoIncrement]
    [Column("StockId")] public int StockId { get; set; }

    [Indexed(Unique = true)]
    [Column("Symbol")] public string Symbol { get; set; } = string.Empty;

    [Column("CompanyName")] public string CompanyName { get; set; } = string.Empty;

    [Column("SharesOutstanding")] public int SharesOutstanding { get; set; }

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
}

public static class StockMapper
{
    public static Stock ToDomain(StockRow r) => new()
    {
        StockId = r.StockId,
        Symbol = r.Symbol,
        CompanyName = r.CompanyName,
        SharesOutstanding = r.SharesOutstanding,
        CreatedAt = r.CreatedAt,
    };

    public static StockRow ToRow(Stock s) => new()
    {
        StockId = s.StockId,
        Symbol = s.Symbol,
        CompanyName = s.CompanyName,
        SharesOutstanding = s.SharesOutstanding,
        CreatedAt = s.CreatedAt,
    };
}
