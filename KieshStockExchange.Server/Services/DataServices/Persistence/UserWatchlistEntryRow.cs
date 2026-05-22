using SQLite;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("UserWatchlist")]
public class UserWatchlistEntryRow
{
    [PrimaryKey, AutoIncrement]
    [Column("Id")] public int Id { get; set; }

    [Indexed(Name = "IX_UserWatchlist_User_Stock", Order = 1, Unique = true)]
    [Column("UserId")] public int UserId { get; set; }

    [Indexed(Name = "IX_UserWatchlist_User_Stock", Order = 2, Unique = true)]
    [Column("StockId")] public int StockId { get; set; }

    [Column("SortOrder")] public int SortOrder { get; set; }

    [Column("AddedAt")] public DateTime AddedAt { get; set; }
}

public static class UserWatchlistEntryMapper
{
    public static UserWatchlistEntry ToDomain(UserWatchlistEntryRow r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        StockId = r.StockId,
        SortOrder = r.SortOrder,
        AddedAt = r.AddedAt,
    };

    public static UserWatchlistEntryRow ToRow(UserWatchlistEntry e) => new()
    {
        Id = e.Id,
        UserId = e.UserId,
        StockId = e.StockId,
        SortOrder = e.SortOrder,
        AddedAt = e.AddedAt,
    };
}
