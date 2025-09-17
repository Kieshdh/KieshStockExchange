using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Transactions")]
public class Transaction : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("TransactionId")] public int TransactionId { get; set; } = 0;

    [Column("StockId")] public int StockId { get; set; } = 0;

    [Column("BuyOrderId")] public int BuyOrderId { get; set; } = 0;

    [Column("SellOrderId")] public int SellOrderId { get; set; } = 0;

    [Column("BuyerId")] public int BuyerId { get; set; } = 0;

    [Column("SellerId")] public int SellerId { get; set; } = 0;

    [Column("Quantity")] public int Quantity { get; set; } = 0;

    [Column("Price")] public decimal Price { get; set; } = 0m;

    [Ignore] public decimal TotalAmount => Price * Quantity;

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    [Column("Timestamp")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => StockId > 0 && BuyerId > 0 && SellerId > 0 && Quantity > 0 &&
        Price > 0 && BuyOrderId > 0 && SellOrderId > 0 && IsValidCurrency(); 

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);
    #endregion

    #region String Representations
    public override string ToString() =>
        $"Transaction #{TransactionId} {Quantity} @ {PriceDisplay} at {TimestampDisplay}";

    [Ignore] public string TimestampDisplay => Timestamp.ToString("dd/MM/yyyy HH:mm:ss");

    [Ignore] public string PriceDisplay => CurrencyHelper.Format(Price, CurrencyType);

    [Ignore] public string TotalAmountDisplay => CurrencyHelper.Format(TotalAmount, CurrencyType);
    #endregion
}