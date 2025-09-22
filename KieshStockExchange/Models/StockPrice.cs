using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("StockPrices")]
public class StockPrice : IValidatable
{
    #region Properties
    private int _priceId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("PriceId")] public int PriceId {
        get => _priceId;
        set { 
            if (_priceId != 0) throw new InvalidOperationException("PriceId is immutable once set.");
            _priceId = value < 0 ? 0 : value;
        }
    }

    private int _stockId = 0;
    [Indexed(Name = "IX_StockPrices_Stock_Curr_Time", Order = 1)]
    [Column("StockId")] public int StockId { 
        get => _stockId;
        set {
            if (_stockId != 0) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    private decimal _price = 0m;
    [Column("Price")] public decimal Price { 
        get => _price;
        set {
            if (_price != 0m) throw new InvalidOperationException("Price is immutable once set.");
            if (value <= 0m) throw new ArgumentOutOfRangeException("Price must be positive.");
            _price = value;
        }
    }

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Indexed(Name = "IX_StockPrices_Stock_Curr_Time", Order = 2)]
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    private DateTime _timeStamp = TimeHelper.NowUtc();
    [Indexed(Name = "IX_StockPrices_Stock_Curr_Time", Order = 3)]
    [Column("Timestamp")] public DateTime Timestamp
    {
        get => _timeStamp;
        set => _timeStamp = TimeHelper.EnsureUtc(value);
    }
    #endregion

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
