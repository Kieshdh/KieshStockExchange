using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("StockListings")]
public class StockListingRow
{
    [PrimaryKey, AutoIncrement]
    [Column("ListingId")] public int ListingId { get; set; }

    [Indexed(Name = "IX_StockListing", Order = 1, Unique = true)]
    [Column("StockId")] public int StockId { get; set; }

    [Indexed(Name = "IX_StockListing", Order = 2, Unique = true)]
    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    [Column("IsPrimary")] public bool IsPrimary { get; set; }

    [Column("SeedPrice")] public decimal SeedPrice { get; set; }

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
}

public static class StockListingMapper
{
    public static StockListing ToDomain(StockListingRow r) => new()
    {
        ListingId = r.ListingId,
        StockId = r.StockId,
        Currency = r.Currency,
        IsPrimary = r.IsPrimary,
        SeedPrice = r.SeedPrice,
        CreatedAt = r.CreatedAt,
    };

    public static StockListingRow ToRow(StockListing l) => new()
    {
        ListingId = l.ListingId,
        StockId = l.StockId,
        Currency = l.Currency,
        IsPrimary = l.IsPrimary,
        SeedPrice = l.SeedPrice,
        CreatedAt = l.CreatedAt,
    };
}
