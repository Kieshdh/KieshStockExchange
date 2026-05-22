using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class StockPrice : IValidatable
{
    private int _priceId = 0;
    public int PriceId
    {
        get => _priceId;
        set
        {
            if (_priceId != 0) throw new InvalidOperationException("PriceId is immutable once set.");
            _priceId = value < 0 ? 0 : value;
        }
    }

    private int _stockId = 0;
    public int StockId
    {
        get => _stockId;
        set
        {
            if (_stockId != 0) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    private decimal _price = 0m;
    public decimal Price
    {
        get => _price;
        set
        {
            if (_price != 0m) throw new InvalidOperationException("Price is immutable once set.");
            if (value <= 0m) throw new ArgumentOutOfRangeException("Price must be positive.");
            _price = value;
        }
    }

    public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    private DateTime _timeStamp = TimeHelper.NowUtc();
    public DateTime Timestamp
    {
        get => _timeStamp;
        set => _timeStamp = TimeHelper.EnsureUtc(value);
    }

    public bool IsValid() => Price > 0 && StockId > 0 && IsValidCurrency();

    public bool IsInvalid => !IsValid();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    public override string ToString() =>
        $"StockPrice #{PriceId}: StockId #{StockId} - Price: {PriceDisplay} at {TimestampDisplay}";
    public string PriceDisplay => CurrencyHelper.Format(Price, CurrencyType);
    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}
