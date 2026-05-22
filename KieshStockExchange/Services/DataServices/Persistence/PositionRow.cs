using SQLite;
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
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
