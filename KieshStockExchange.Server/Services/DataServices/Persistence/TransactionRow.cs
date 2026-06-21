using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Transactions")]
public class TransactionRow
{
    [PrimaryKey, AutoIncrement]
    [Column("TransactionId")] public int TransactionId { get; set; }

    [Indexed(Name = "IX_Tx_Stock_Curr_Time", Order = 1)]
    [Column("StockId")] public int StockId { get; set; }

    [Column("BuyOrderId")] public int BuyOrderId { get; set; }

    [Column("SellOrderId")] public int SellOrderId { get; set; }

    [Indexed]
    [Column("BuyerId")] public int BuyerId { get; set; }

    [Indexed]
    [Column("SellerId")] public int SellerId { get; set; }

    [Column("Quantity")] public int Quantity { get; set; }

    [Column("Price")] public decimal Price { get; set; }

    // §bounce: nullable bounce-free reference price (mid/micro) captured at trade time. Null when
    // the flag is off ⇒ scorer/candle fall back to Price. See Transaction.MidPrice.
    [Column("MidPrice")] public decimal? MidPrice { get; set; }

    [Indexed(Name = "IX_Tx_Stock_Curr_Time", Order = 2)]
    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    [Indexed(Name = "IX_Tx_Stock_Curr_Time", Order = 3)]
    [Column("Timestamp")] public DateTime Timestamp { get; set; }
}

public static class TransactionMapper
{
    public static Transaction ToDomain(TransactionRow r) => new()
    {
        TransactionId = r.TransactionId,
        StockId = r.StockId,
        BuyOrderId = r.BuyOrderId,
        SellOrderId = r.SellOrderId,
        BuyerId = r.BuyerId,
        SellerId = r.SellerId,
        Quantity = r.Quantity,
        Price = r.Price,
        MidPrice = r.MidPrice,
        Currency = r.Currency,
        Timestamp = r.Timestamp,
    };

    public static TransactionRow ToRow(Transaction t) => new()
    {
        TransactionId = t.TransactionId,
        StockId = t.StockId,
        BuyOrderId = t.BuyOrderId,
        SellOrderId = t.SellOrderId,
        BuyerId = t.BuyerId,
        SellerId = t.SellerId,
        Quantity = t.Quantity,
        Price = t.Price,
        MidPrice = t.MidPrice,
        Currency = t.Currency,
        Timestamp = t.Timestamp,
    };
}
