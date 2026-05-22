using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Transaction : IValidatable
{
    private int _transactionId = 0;
    public int TransactionId
    {
        get => _transactionId;
        set
        {
            if (_transactionId != 0 && value != _transactionId) throw new InvalidOperationException("TransactionId is immutable once set.");
            _transactionId = value < 0 ? 0 : value;
        }
    }

    private int _stockId = 0;
    public int StockId
    {
        get => _stockId;
        set
        {
            if (_stockId != 0 && value != _stockId) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    private int _buyOrderId = 0;
    public int BuyOrderId
    {
        get => _buyOrderId;
        set
        {
            if (_buyOrderId != 0 && value != _buyOrderId) throw new InvalidOperationException("BuyOrderId is immutable once set.");
            _buyOrderId = value;
        }
    }

    private int _sellOrderId = 0;
    public int SellOrderId
    {
        get => _sellOrderId;
        set
        {
            if (_sellOrderId != 0 && value != _sellOrderId) throw new InvalidOperationException("SellOrderId is immutable once set.");
            _sellOrderId = value;
        }
    }

    private int _buyerId = 0;
    public int BuyerId
    {
        get => _buyerId;
        set
        {
            if (_buyerId != 0 && value != _buyerId) throw new InvalidOperationException("BuyerId is immutable once set.");
            _buyerId = value;
        }
    }

    private int _sellerId = 0;
    public int SellerId
    {
        get => _sellerId;
        set
        {
            if (_sellerId != 0 && value != _sellerId) throw new InvalidOperationException("SellerId is immutable once set.");
            _sellerId = value;
        }
    }

    private int _quantity = 0;
    public int Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity != 0 && value != _quantity) throw new InvalidOperationException("Quantity is immutable once set.");
            if (value <= 0) throw new ArgumentException("Quantity must be positive.");
            _quantity = value <= 0 ? 0 : value;
        }
    }

    private decimal _price = 0m;
    public decimal Price
    {
        get => _price;
        set
        {
            if (_price != 0m && value != _price) throw new InvalidOperationException("Price is immutable once set.");
            if (value <= 0m) throw new ArgumentException("Price must be positive.");
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

    public bool IsValid() => StockId > 0 && Quantity > 0 && Price > 0 && IsValidParticipants() &&
        BuyOrderId > 0 && SellOrderId > 0 && IsValidCurrency() && IsValidTimestamp();

    public bool IsInvalid => !IsValid();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidTimestamp() => Timestamp > DateTime.MinValue && Timestamp <= TimeHelper.NowUtc();

    private bool IsValidParticipants() => BuyerId != SellerId && BuyerId > 0 && SellerId > 0;

    public override string ToString() =>
        $"Transaction #{TransactionId} - {Quantity} @ {PriceDisplay} at {TimestampDisplay}";

    public string TimestampDisplay => Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string TimestampShort => Timestamp.ToLocalTime().ToString("dd-MM HH:mm");

    public string PriceDisplay => CurrencyHelper.Format(Price, CurrencyType);

    public string TotalAmountDisplay => CurrencyHelper.Format(TotalAmount, CurrencyType);

    public decimal TotalAmount => CurrencyHelper.Notional(Price, Quantity, CurrencyType);

    public bool InvolvesUser(int userId) => BuyerId == userId || SellerId == userId;
}
