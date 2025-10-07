using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Orders")]
public class Order : IValidatable
{
    #region Constants
    public static class Types
    {
        public const string MarketBuy = "MarketBuy";
        public const string MarketSell = "MarketSell";
        public const string LimitBuy = "LimitBuy";
        public const string LimitSell = "LimitSell";
    }

    public static class Statuses
    {
        public const string Open = "Open";
        public const string Filled = "Filled";
        public const string Cancelled = "Cancelled";
    }
    #endregion

    #region Properties
    private int _orderId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("OrderId")] public int OrderId {
        get => _orderId;
        set {
            if (_orderId != 0) throw new InvalidOperationException("OrderId is immutable once set.");
            _orderId = value < 0 ? 0 : value;
        }
    }

    private int _userId = 0;
    [Indexed(Name = "IX_Orders_User_Status", Order = 1)]
    [Column("UserId")] public int UserId { 
        get => _userId; 
        set {
            if (_userId != 0) throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    private int _stockId = 0;
    [Indexed(Name = "IX_Orders_Stock_Status", Order = 1)]
    [Column("StockId")] public int StockId { 
        get => _stockId; 
        set {
            if (_stockId != 0) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    private int _quantity = 0;
    [Column("Quantity")] public int Quantity { 
        get => _quantity; 
        set => _quantity = value;
    }

    private decimal _price = 0;
    [Column("Price")] public decimal Price { 
        get => _price; 
        set {
            if (value <= 0m) throw new ArgumentException("Price must be positive.");
            _price = value;
        }
    }

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    // "MarketBuy", "MarketSell", "LimitBuy", "LimitSell"
    private string _orderType = String.Empty;
    [Column("OrderType")] public string OrderType { 
        get => _orderType; 
        set => _orderType = value;
    }

    // "Open", "Filled", "Cancelled"
    private string _status = Statuses.Open;
    [Indexed(Name = "IX_Orders_Stock_Status", Order = 2)]

    [Indexed(Name = "IX_Orders_User_Status", Order = 2)]
    [Column("Status")] public string Status { 
        get => _status; 
        set => _status = value;
    }

    private int _amountFilled = 0;
    [Column("AmountFilled")] public int AmountFilled { 
        get => _amountFilled; 
        set => _amountFilled = value;
    }

    private DateTime _createdAt = TimeHelper.NowUtc();
    [Column("CreatedAt")] public DateTime CreatedAt {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    private DateTime _updatedAt = TimeHelper.NowUtc();
    [Column("UpdatedAt")] public DateTime UpdatedAt {
        get => _updatedAt;
        set => _updatedAt = TimeHelper.EnsureUtc(value);
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && StockId > 0 && Quantity > 0 && Price > 0 &&
        IsValidOrderType() && IsValidStatus() && IsValidCurrency() && IsValidAmount();

    private bool IsValidOrderType() =>
        OrderType == Types.MarketBuy || OrderType == Types.MarketSell ||
        OrderType == Types.LimitBuy || OrderType == Types.LimitSell;
    private bool IsValidStatus() =>
        Status == Statuses.Open || Status == Statuses.Filled || Status == Statuses.Cancelled;
    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidAmount() =>
        (IsFilled && AmountFilled == Quantity) || 
        (IsOpen && RemainingQuantity > 0) || 
        (IsCancelled && AmountFilled != Quantity);

    #endregion

    #region String Representations
    public override string ToString() =>
        $"Order #{OrderId} - {OrderType} {Quantity} @ {PriceDisplay} - Status: {Status}";
    [Ignore] public string PriceDisplay => CurrencyHelper.Format(Price, CurrencyType);
    [Ignore] public string TotalAmountDisplay => CurrencyHelper.Format(TotalAmount, CurrencyType);
    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string AmountFilledDisplay => $"{AmountFilled}/{Quantity}";
    #endregion

    #region Helper Variables
    [Ignore] public bool IsBuyOrder =>
        OrderType == Types.MarketBuy || OrderType == Types.LimitBuy;
    [Ignore] public bool IsSellOrder =>
        OrderType == Types.MarketSell || OrderType == Types.LimitSell;
    [Ignore] public bool IsLimitOrder =>
        OrderType == Types.LimitBuy || OrderType == Types.LimitSell;
    [Ignore] public bool IsMarketOrder =>
        OrderType == Types.MarketBuy || OrderType == Types.MarketSell;
    [Ignore] public bool IsOpen => Status == Statuses.Open;
    [Ignore] public bool IsFilled => Status == Statuses.Filled;
    [Ignore] public bool IsCancelled => Status == Statuses.Cancelled;
    [Ignore] public decimal TotalAmount => Price * Quantity;
    [Ignore] public int RemainingQuantity => Quantity - AmountFilled;
    [Ignore] public decimal RemainingAmount => RemainingQuantity * Price;
    #endregion

    #region Order Operations
    public void Fill(int quantity)
    {
        if (!IsOpen) throw new InvalidOperationException("Order is not open for filling.");
        if (quantity <= 0 || quantity > RemainingQuantity)
            throw new ArgumentException("Invalid fill quantity.");
        AmountFilled += quantity;
        if (AmountFilled == Quantity)
            Status = Statuses.Filled;
        UpdatedAt = TimeHelper.NowUtc();
    }
    public void Cancel()
    {
        if (!IsOpen) throw new InvalidOperationException("Only open orders can be cancelled.");
        Status = Statuses.Cancelled;
        UpdatedAt = TimeHelper.NowUtc();
    }
    public void UpdatePrice(decimal newPrice)
    {
        if (!IsOpen) throw new InvalidOperationException("Cannot update price of a non-open order.");
        if (!IsLimitOrder) throw new InvalidOperationException("Only limit orders can have their price updated.");
        if (newPrice <= 0) throw new ArgumentException("Price must be greater than zero.");
        Price = newPrice;
        UpdatedAt = TimeHelper.NowUtc();
    }
    public void UpdateQuantity(int newQuantity)
    {
        if (!IsOpen) throw new InvalidOperationException("Cannot update quantity of a non-open order.");
        if (newQuantity < AmountFilled)
            throw new ArgumentException("New quantity cannot be less than amount filled.");
        Quantity = newQuantity;
        UpdatedAt = TimeHelper.NowUtc();
        if (AmountFilled == Quantity)
            Status = Statuses.Filled; 
    }
    #endregion
}
