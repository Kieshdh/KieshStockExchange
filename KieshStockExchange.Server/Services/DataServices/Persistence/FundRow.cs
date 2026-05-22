using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Funds")]
public class FundRow
{
    [PrimaryKey, AutoIncrement]
    [Column("FundId")] public int FundId { get; set; }

    [Indexed(Name = "IX_Funds_User_Currency", Order = 1, Unique = true)]
    [Column("UserId")] public int UserId { get; set; }

    [Column("TotalBalance")] public decimal TotalBalance { get; set; }

    [Column("ReservedBalance")] public decimal ReservedBalance { get; set; }

    [Indexed(Name = "IX_Funds_User_Currency", Order = 2, Unique = true)]
    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
}

public static class FundMapper
{
    public static Fund ToDomain(FundRow r) => new()
    {
        FundId = r.FundId,
        UserId = r.UserId,
        TotalBalance = r.TotalBalance,
        ReservedBalance = r.ReservedBalance,
        Currency = r.Currency,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    public static FundRow ToRow(Fund f) => new()
    {
        FundId = f.FundId,
        UserId = f.UserId,
        TotalBalance = f.TotalBalance,
        ReservedBalance = f.ReservedBalance,
        Currency = f.Currency,
        CreatedAt = f.CreatedAt,
        UpdatedAt = f.UpdatedAt,
    };
}
