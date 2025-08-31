using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("StockPrices")]
public class StockPrice : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("PriceId")] public int PriceId { get; set; }

    [Column("StockId")] public int StockId { get; set; }

    [Column("Price")] public decimal Price { get; set; }

    [Ignore] public CurrencyType CurrencyType { get; set; }
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    [Column("Timestamp")] public DateTime Timestamp { get; set; }
    #endregion

    public StockPrice()
    {
        Timestamp = DateTime.UtcNow;
        CurrencyType = CurrencyType.USD; // Default currency
    }

    #region IValidatable Implementation
    public bool IsValid() => Price > 0 && StockId > 0 && IsValidCurrency();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);
    #endregion

    #region String Representations
    public override string ToString() =>
        $"StockPrice #{PriceId}: StockId #{StockId} - Price: {PriceDisplay} at {Timestamp}";
    [Ignore] public string PriceDisplay => CurrencyHelper.Format(Price, CurrencyType);
    [Ignore] public string TimestampDisplay => Timestamp.ToString("dd/MM/yyyy HH:mm:ss");
    #endregion

}
