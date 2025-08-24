using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models;

[Table("Transactions")]
public class Transaction : IValidatable
{
    #region Constants
    public static class Currencies
    {
        public const string USD = "USD";
        public const string EUR = "EURO";
    }
    #endregion

    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("TransactionId")] public int TransactionId { get; set; }

    [Column("StockId")] public int StockId { get; set; }

    [Column("BuyOrderId")] public int BuyOrderId { get; set; }

    [Column("SellOrderId")] public int SellOrderId { get; set; }

    [Column("BuyerId")] public int BuyerId { get; set; }

    [Column("SellerId")] public int SellerId { get; set; }

    [Column("Quantity")] public int Quantity { get; set; }

    [Column("Price")] public decimal Price { get; set; }

    // "USD", "EUR"
    [Column("Currency")] public string Currency { get; set; }

    [Column("Timestamp")] public DateTime Timestamp { get; set; }
    #endregion

    public Transaction()
    {
        Timestamp = DateTime.UtcNow;
        Currency = Currencies.USD; // Default currency
    }
    #region IValidatable Implementation
    public bool IsValid() =>
        StockId > 0 && BuyOrderId > 0 && SellOrderId > 0 && IsValidCurrency() &&
        BuyerId > 0 && SellerId > 0 && Quantity > 0 && Price > 0;

    private bool IsValidCurrency() =>
        Currency == Currencies.USD || Currency == Currencies.EUR;
    #endregion

    #region String Representations
    public override string ToString() =>
        $"Transaction #{TransactionId} {Quantity} @ {PriceString()} at {TimestampString()}";

    public string TimestampString() =>
        Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

    public string PriceString() =>
        $"{Price.ToString("C", System.Globalization.CultureInfo.CurrentCulture)} {Currency}";
    #endregion
}