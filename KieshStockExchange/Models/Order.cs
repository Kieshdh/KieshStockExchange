using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Orders")]
public class Order : IValidatable
{
    #region Constants
    public static class Types
    {
        public const string LimitBuy = "LimitBuy";
        public const string LimitSell = "LimitSell";
        public const string TrueMarketBuy = "TrueMarketBuy";
        public const string TrueMarketSell = "TrueMarketSell";
        public const string SlippageMarketBuy = "SlippageMarketBuy";
        public const string SlippageMarketSell = "SlippageMarketSell";
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
            if (value < 0m) throw new ArgumentException("Price cannot be negative.");
            _price = value;
        }
    }

    private decimal? _slippagePercent = null;
    [Column("Slippage")] public decimal? SlippagePercent {
        get => _slippagePercent;
        set {
            if (value.HasValue && (value.Value < 0m || value.Value > 100m))
                throw new ArgumentException("Slippage percent must be between 0 and 100.");
            _slippagePercent = value;
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
        set {
            if (value is Types.TrueMarketBuy or Types.SlippageMarketBuy or Types.LimitBuy  or
                Types.TrueMarketSell or Types.SlippageMarketSell or Types.LimitSell)
                _orderType = value;
            else throw new ArgumentException("Invalid OrderType.");
        }
    }

    // "Open", "Filled", "Cancelled"
    private string _status = Statuses.Open;
    [Indexed(Name = "IX_Orders_Stock_Status", Order = 2)]

    [Indexed(Name = "IX_Orders_User_Status", Order = 2)]
    [Column("Status")] public string Status { 
        get => _status;
        set {
            if (value == Statuses.Open || value == Statuses.Filled ||
                value == Statuses.Cancelled)
                _status = value;
            else throw new ArgumentException("Invalid Status.");
        }
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
    public bool IsValid() => UserId > 0 && StockId > 0 && Quantity > 0 && IsValidPrice() &&
        IsValidOrderType() && IsValidStatus() && IsValidCurrency() && IsValidAmount();

    public bool IsInvalid => !IsValid();

    private bool IsValidOrderType() => IsLimitOrder || IsMarketOrder;
    private bool IsValidStatus() =>
        Status == Statuses.Open || Status == Statuses.Filled || Status == Statuses.Cancelled;
    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidAmount() => (IsFilled && AmountFilled == Quantity) || 
        (IsOpen && RemainingQuantity > 0) || (IsCancelled && AmountFilled != Quantity);

    private bool IsValidPrice() => (IsLimitOrder && SlippagePercent is null && Price > 0m) || 
        (IsTrueMarketOrder && SlippagePercent is null && Price == 0m) || (IsSlippageOrder && SlippagePercent.HasValue && Price > 0m);

    #endregion

    #region String Representations
    public override string ToString() =>
        $"Order #{OrderId} - {OrderType} - {Quantity} @ {PriceDisplay} - Status: {Status}";
    [Ignore] public string PriceDisplay
    {
        get
        {
            if (IsLimitOrder) return CurrencyHelper.Format(Price, CurrencyType);
            if (IsTrueMarketOrder) return "MARKET";

            // Slippage market
            var dir = IsBuyOrder ? "≤" : "≥";
            var cap = CurrencyHelper.Format(PriceWithSlippage, CurrencyType);
            var anchor = CurrencyHelper.Format(Price, CurrencyType);
            return $"MKT {dir}{cap} • anchor {anchor}";
        }
    }
    [Ignore] public string TotalAmountDisplay
    {
        get
        {
            if (IsLimitOrder) return CurrencyHelper.Format(TotalAmount, CurrencyType);
            if (IsTrueMarketOrder) return "-"; // Unknown total amount

            // Slippage market
            var dir = IsBuyOrder ? "≤" : "≥";
            var cap = CurrencyHelper.Format(TotalAmount, CurrencyType);
            return $"MKT {dir}{cap}";
        }
    }
    [Ignore] public string AmountFilledDisplay => $"{AmountFilled}/{Quantity}";
    [Ignore] public string SideDisplay => IsBuyOrder ? "BUY" : "SELL";
    [Ignore] public string TypeDisplay => IsLimitOrder ? "LIMIT" : (IsTrueMarketOrder ? "MKT" : "MKT±");
    [Ignore] public string StatusDisplay => Status.ToUpperInvariant();
    [Ignore]
    public string SlippageDisplay => SlippagePercent.HasValue ? $"±{SlippagePercent.Value:0.##}%" : "—";
    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string CreatedDateShort => CreatedAt.ToString("dd-MM HH:mm");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string UpdatedDateShort => UpdatedAt.ToString("dd-MM HH:mm");
    #endregion

    #region Helper Variables
    [Ignore] public bool IsBuyOrder => OrderType is Types.TrueMarketBuy or Types.SlippageMarketBuy or Types.LimitBuy;
    [Ignore] public bool IsSellOrder => OrderType is Types.TrueMarketSell or Types.SlippageMarketSell or Types.LimitSell;
    [Ignore] public bool IsLimitOrder =>
        OrderType == Types.LimitBuy || OrderType == Types.LimitSell;
    [Ignore] public bool IsMarketOrder => IsSlippageOrder || IsTrueMarketOrder;
    [Ignore] public bool IsSlippageOrder =>
        OrderType == Types.SlippageMarketBuy || OrderType == Types.SlippageMarketSell;
    [Ignore] public bool IsTrueMarketOrder =>
        OrderType == Types.TrueMarketBuy || OrderType == Types.TrueMarketSell;
    [Ignore] public bool IsOpen => Status == Statuses.Open;
    [Ignore] public bool IsClosed => !IsOpen;
    [Ignore] public bool IsFilled => Status == Statuses.Filled;
    [Ignore] public bool IsCancelled => Status == Statuses.Cancelled;
    [Ignore] public decimal TotalAmount => IsLimitOrder ? Price * Quantity
        : (IsSlippageOrder && SlippagePercent.HasValue) ? PriceWithSlippage!.Value * Quantity : 0m;
    [Ignore] public int RemainingQuantity => Quantity - AmountFilled;
    [Ignore] public decimal RemainingAmount => IsLimitOrder ? Price * RemainingQuantity
        : (IsSlippageOrder && SlippagePercent.HasValue) ? PriceWithSlippage!.Value * RemainingQuantity : 0m;
    [Ignore] public decimal FilledAmount => IsLimitOrder ? Price * AmountFilled
        : (IsSlippageOrder && SlippagePercent.HasValue) ? PriceWithSlippage!.Value * AmountFilled : 0m;
    [Ignore] public decimal? SlippageAmount => 
        SlippagePercent.HasValue ? (SlippagePercent.Value / 100m) * Price : null;
    [Ignore] public decimal? PriceWithSlippage
    {
        get
        {
            if (!SlippageAmount.HasValue) return null;
            var raw = IsBuyOrder ? Price + SlippageAmount.Value : Price - SlippageAmount.Value;
            if (raw <= 0m) return 0m;
            return CurrencyHelper.RoundMoney(raw, CurrencyType);
        }
    }
    [Ignore] public decimal? EffectiveTakerLimit => IsSlippageOrder ? PriceWithSlippage : (IsLimitOrder ? Price : null);
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
        if (!IsLimitOrder) throw new InvalidOperationException("Only limit orders can have their quantity updated.");
        if (newQuantity < AmountFilled)
            throw new ArgumentException("New quantity cannot be less than amount filled.");
        Quantity = newQuantity;
        UpdatedAt = TimeHelper.NowUtc();
        if (AmountFilled == Quantity)
            Status = Statuses.Filled; 
    }

    public Order Clone() =>
        new() {
            UserId = this.UserId,
            StockId = this.StockId,
            Quantity = this.Quantity,
            Price = this.Price,
            SlippagePercent = this.SlippagePercent,
            CurrencyType = this.CurrencyType,
            OrderType = this.OrderType,
            Status = this.Status,
            AmountFilled = this.AmountFilled,
            CreatedAt = this.CreatedAt,
            UpdatedAt = this.UpdatedAt
        };

    public Order CloneFull()
    {         
        var clone = Clone();
        clone.OrderId = OrderId;
        return clone;
    }
    #endregion
}
