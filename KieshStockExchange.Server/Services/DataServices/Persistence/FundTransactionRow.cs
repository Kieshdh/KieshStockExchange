using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("FundTransactions")]
public class FundTransactionRow
{
    [PrimaryKey, AutoIncrement]
    [Column("FundTransactionId")] public int FundTransactionId { get; set; }

    [Indexed(Name = "IX_FundTx_User_Time", Order = 1)]
    [Column("UserId")] public int UserId { get; set; }

    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    [Column("Amount")] public decimal Amount { get; set; }

    [Column("Kind")] public string Kind { get; set; } = FundTransaction.Kinds.Deposit;

    [Column("Note")] public string? Note { get; set; }

    [Indexed(Name = "IX_FundTx_User_Time", Order = 2)]
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
}

public static class FundTransactionMapper
{
    public static FundTransaction ToDomain(FundTransactionRow r) => new()
    {
        FundTransactionId = r.FundTransactionId,
        UserId = r.UserId,
        Currency = r.Currency,
        Amount = r.Amount,
        Kind = r.Kind,
        Note = r.Note,
        CreatedAt = r.CreatedAt,
    };

    public static FundTransactionRow ToRow(FundTransaction t) => new()
    {
        FundTransactionId = t.FundTransactionId,
        UserId = t.UserId,
        Currency = t.Currency,
        Amount = t.Amount,
        Kind = t.Kind,
        Note = t.Note,
        CreatedAt = t.CreatedAt,
    };
}
