using SQLite;

namespace KieshStockExchange.Models;

[Table("StockPrices")]
public class StockPrice : IValidatable
{
    #region Constants
    public static class Currencies
    {
        public const string Usd = "USD";
        public const string Eur = "EUR";
    }

    private const decimal ConversionRate = 1.1611378m;
    #endregion

    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("PriceId")] public int PriceId { get; set; }

    [Column("StockId")] public int StockId { get; set; }

    [Column("Price")] public decimal Price { get; set; }

    // "USD", "EUR"
    [Column("Currency")] public string Currency { get; set; } 

    [Column("Timestamp")] public DateTime Timestamp { get; set; }
    #endregion

    public StockPrice()
    {
        Timestamp = DateTime.UtcNow;
        Currency = Currencies.Usd; // Default currency
    }

    #region IValidatable Implementation
    public bool IsValid() => 
        Price > 0 && StockId > 0 && (Currency == "USD" || Currency == "EUR");
    #endregion

    #region String Representations
    public decimal PriceUSD => Currency == "USD" ?
          Math.Round(Price, 2) : Math.Round(Price * ConversionRate, 2);

    public decimal PriceEUR => Currency == "EUR" ?
          Math.Round(Price, 2) : Math.Round(Price / ConversionRate, 2);

    private string _currencySymbol => (Currency == "USD") ? "$" : "€"; 
    public override string ToString() =>
        $"StockPrice #{PriceId}: StockId #{StockId} - Price: {PriceString(true)} at {Timestamp}";
    public string PriceString(bool includeCurrencySymbol) =>
        includeCurrencySymbol ? 
        $"{_currencySymbol} {Math.Round(Price, 2)}" :
        $"{Math.Round(Price, 2)}";
    #endregion

}
