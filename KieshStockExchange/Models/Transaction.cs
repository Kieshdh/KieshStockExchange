using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Transactions")]
public class Transaction : IValidatable
{
    #region Properties
    private int _transactionId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("TransactionId")] public int TransactionId { 
        get => _transactionId;
        set {
            if (_transactionId != 0 && value != _transactionId) throw new InvalidOperationException("TransactionId is immutable once set.");
            _transactionId = value < 0 ? 0 : value;
        }
    }

    private int _stockId = 0;
    [Indexed(Name = "IX_Tx_Stock_Curr_Time", Order = 1)]
    [Column("StockId")] public int StockId { 
        get => _stockId;
        set {
            if (_stockId != 0 && value != _stockId) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    private int _buyOrderId = 0;
    [Column("BuyOrderId")] public int BuyOrderId {
        get => _buyOrderId;
        set {
            if (_buyOrderId != 0 && value != _buyOrderId) throw new InvalidOperationException("BuyOrderId is immutable once set.");
            _buyOrderId = value;
        }
    }

    private int _sellOrderId = 0;
    [Column("SellOrderId")] public int SellOrderId { 
        get => _sellOrderId;
        set {
            if (_sellOrderId != 0 && value != _sellOrderId) throw new InvalidOperationException("SellOrderId is immutable once set.");
            _sellOrderId = value;
        }
    }

    private int _buyerId = 0;
    [Indexed]
    [Column("BuyerId")] public int BuyerId { 
        get => _buyerId;
        set {
            if (_buyerId != 0 && value != _buyerId) throw new InvalidOperationException("BuyerId is immutable once set.");
            _buyerId = value;
        }
    }

    private int _sellerId = 0;
    [Indexed]
    [Column("SellerId")] public int SellerId { 
        get => _sellerId;
        set {
            if (_sellerId != 0 && value != _sellerId) throw new InvalidOperationException("SellerId is immutable once set.");
            _sellerId = value;
        }
    }

    private int _quantity = 0;
    [Column("Quantity")] public int Quantity { 
        get => _quantity;
        set {
            if (_quantity != 0 && value != _quantity) throw new InvalidOperationException("Quantity is immutable once set.");
            if (value <= 0) throw new ArgumentException("Quantity must be positive.");
            _quantity = value <= 0 ? 0 : value;
        }
    }

    private decimal _price = 0m;
    [Column("Price")] public decimal Price { 
        get => _price;
        set {
            if (_price != 0m && value != _price) throw new InvalidOperationException("Price is immutable once set.");
            if (value <= 0m) throw new ArgumentException("Price must be positive.");
            _price = value;
        }
    }

    [Ignore] public decimal TotalAmount => Price * Quantity;

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Indexed(Name = "IX_Tx_Stock_Curr_Time", Order = 2)]
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    private DateTime _timeStamp = TimeHelper.NowUtc();
    [Indexed(Name = "IX_Tx_Stock_Curr_Time", Order = 3)]
    [Column("Timestamp")] public DateTime Timestamp {
        get => _timeStamp;
        set => _timeStamp = TimeHelper.EnsureUtc(value);
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => StockId > 0 && Quantity > 0 && Price > 0 && IsValidParticipants() &&
        BuyOrderId > 0 && SellOrderId > 0 && IsValidCurrency() && IsValidTimestamp();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidTimestamp() => Timestamp > DateTime.MinValue && Timestamp <= TimeHelper.NowUtc();
    
    private bool IsValidParticipants() => BuyerId != SellerId && BuyerId > 0 && SellerId > 0;

    #endregion

    #region String Representations
    public override string ToString() =>
        $"Transaction #{TransactionId} {Quantity} @ {PriceDisplay} at {TimestampDisplay}";

    [Ignore] public string TimestampDisplay => Timestamp.ToString("dd/MM/yyyy HH:mm:ss");

    [Ignore] public string PriceDisplay => CurrencyHelper.Format(Price, CurrencyType);

    [Ignore] public string TotalAmountDisplay => CurrencyHelper.Format(TotalAmount, CurrencyType);
    #endregion
}