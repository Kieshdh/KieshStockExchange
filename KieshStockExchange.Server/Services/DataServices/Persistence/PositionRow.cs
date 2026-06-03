using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Positions")]
public class PositionRow
{
    [PrimaryKey, AutoIncrement]
    [Column("PositionId")] public int PositionId { get; set; }

    [Indexed(Name = "IX_Positions_User_Stock", Order = 1, Unique = true)]
    [Column("UserId")] public int UserId { get; set; }

    [Indexed(Name = "IX_Positions_User_Stock", Order = 2, Unique = true)]
    [Column("StockId")] public int StockId { get; set; }

    [Column("Quantity")] public int Quantity { get; set; }

    [Column("ReservedQuantity")] public int ReservedQuantity { get; set; }

    // §3.6 P1 shorts: cash collateral locked against a negative Quantity, and the
    // currency it is reserved in (maps back to the right per-currency Fund).
    [Column("ShortCollateral")] public decimal ShortCollateral { get; set; }

    [Column("ShortCollateralCurrency")] public string ShortCollateralCurrency { get; set; } = nameof(CurrencyType.USD);

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
}

public static class PositionMapper
{
    public static Position ToDomain(PositionRow r) => new()
    {
        PositionId = r.PositionId,
        UserId = r.UserId,
        StockId = r.StockId,
        Quantity = r.Quantity,
        ReservedQuantity = r.ReservedQuantity,
        ShortCollateral = r.ShortCollateral,
        ShortCollateralCurrencyCode = r.ShortCollateralCurrency,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    public static PositionRow ToRow(Position p) => new()
    {
        PositionId = p.PositionId,
        UserId = p.UserId,
        StockId = p.StockId,
        Quantity = p.Quantity,
        ReservedQuantity = p.ReservedQuantity,
        ShortCollateral = p.ShortCollateral,
        ShortCollateralCurrency = p.ShortCollateralCurrencyCode,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
