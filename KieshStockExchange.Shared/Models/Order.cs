using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Order : IValidatable
{
    public static class Types
    {
        public const string LimitBuy = "LimitBuy";
        public const string LimitSell = "LimitSell";
        public const string TrueMarketBuy = "TrueMarketBuy";
        public const string TrueMarketSell = "TrueMarketSell";
        public const string SlippageMarketBuy = "SlippageMarketBuy";
        public const string SlippageMarketSell = "SlippageMarketSell";
        // §3.6 P2 stop orders. Armed (Status=Pending) off-book until the live price crosses
        // StopPrice, then promoted to the active type below (StopMarket→TrueMarket,
        // StopLimit→Limit) and matched via the normal path.
        public const string StopMarketBuy = "StopMarketBuy";
        public const string StopMarketSell = "StopMarketSell";
        public const string StopLimitBuy = "StopLimitBuy";
        public const string StopLimitSell = "StopLimitSell";
    }

    public static class Statuses
    {
        public const string Open = "Open";
        public const string Filled = "Filled";
        public const string Cancelled = "Cancelled";
        // §3.6 P2: an armed stop — persisted, reservation held, but NOT on the book and
        // invisible to the matcher until the trigger watcher promotes it to Open.
        public const string Pending = "Pending";
    }

    private int _orderId = 0;
    public int OrderId
    {
        get => _orderId;
        set
        {
            if (_orderId != 0 && value != _orderId) throw new InvalidOperationException("OrderId is immutable once set.");
            _orderId = value < 0 ? 0 : value;
        }
    }

    private int _userId = 0;
    public int UserId
    {
        get => _userId;
        set
        {
            if (_userId != 0 && value != _userId) throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
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

    private int _quantity = 0;
    public int Quantity
    {
        get => _quantity;
        set => _quantity = value;
    }

    private decimal _price = 0;
    public decimal Price
    {
        get => _price;
        set
        {
            if (value < 0m) throw new ArgumentException("Price cannot be negative.");
            _price = value;
        }
    }

    private decimal? _slippagePercent = null;
    public decimal? SlippagePercent
    {
        get => _slippagePercent;
        set
        {
            if (value.HasValue && (value.Value < 0m || value.Value > 100m))
                throw new ArgumentException("Slippage percent must be between 0 and 100.");
            _slippagePercent = value;
        }
    }

    // §3.6 P2: the trigger level for stop orders. Non-null only for stop types; the watcher
    // fires when the live price crosses it (sell-stop: price ≤ StopPrice; buy-stop: ≥).
    private decimal? _stopPrice = null;
    public decimal? StopPrice
    {
        get => _stopPrice;
        set
        {
            if (value.HasValue && value.Value < 0m)
                throw new ArgumentException("Stop price cannot be negative.");
            _stopPrice = value;
        }
    }

    private decimal? _buyBudget = null;
    public decimal? BuyBudget
    {
        get => _buyBudget;
        set
        {
            if (value.HasValue && value.Value < 0m)
                throw new ArgumentException("Buy budget cannot be negative.");
            _buyBudget = value;
        }
    }

    public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    private string _orderType = String.Empty;
    public string OrderType
    {
        get => _orderType;
        set
        {
            if (value is Types.TrueMarketBuy or Types.SlippageMarketBuy or Types.LimitBuy or
                Types.TrueMarketSell or Types.SlippageMarketSell or Types.LimitSell or
                Types.StopMarketBuy or Types.StopMarketSell or
                Types.StopLimitBuy or Types.StopLimitSell)
                _orderType = value;
            else throw new ArgumentException("Invalid OrderType.");
        }
    }

    // "Open", "Filled", "Cancelled"
    private string _status = Statuses.Open;
    public string Status
    {
        get => _status;
        set
        {
            if (value == Statuses.Open || value == Statuses.Filled ||
                value == Statuses.Cancelled || value == Statuses.Pending)
                _status = value;
            else throw new ArgumentException("Invalid Status.");
        }
    }

    private int _amountFilled = 0;
    public int AmountFilled
    {
        get => _amountFilled;
        set => _amountFilled = value;
    }

    private DateTime _createdAt = TimeHelper.NowUtc();
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => _createdAt = TimeHelper.EnsureUtc(value);
    }

    private DateTime _updatedAt = TimeHelper.NowUtc();
    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set => _updatedAt = TimeHelper.EnsureUtc(value);
    }

    // Runtime-only reservation tracking, mirrored against Fund.ReservedBalance /
    // Position.ReservedQuantity. Hydrated at cold-load from ReservationMath and
    // maintained in lock-step by every reservation site under the per-user gate.
    public decimal CurrentBuyReservation { get; private set; } = 0m;
    public int CurrentSellReservedQty { get; private set; } = 0;

    public bool IsValid() => UserId > 0 && StockId > 0 && Quantity > 0 && IsValidPrice() &&
        IsValidOrderType() && IsValidStatus() && IsValidCurrency() && IsValidAmount() && IsValidBuyBudget();

    public bool IsInvalid => !IsValid();

    private bool IsValidOrderType() => IsLimitOrder || IsMarketOrder || IsStopOrder;
    private bool IsValidStatus() =>
        Status == Statuses.Open || Status == Statuses.Filled ||
        Status == Statuses.Cancelled || Status == Statuses.Pending;
    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    // An armed (Pending) stop has nothing filled yet; otherwise the normal rules apply.
    private bool IsValidAmount() => (IsFilled && AmountFilled == Quantity) ||
        (IsOpen && RemainingQuantity > 0) || (IsArmed && AmountFilled == 0 && Quantity > 0) ||
        (IsCancelled && AmountFilled >= 0 && AmountFilled < Quantity);

    private bool IsValidPrice() => (IsLimitOrder && SlippagePercent is null && Price > 0m) ||
        (IsTrueMarketOrder && SlippagePercent is null && Price == 0m) ||
        (IsSlippageOrder && SlippagePercent.HasValue && Price > 0m) ||
        // StopMarket fires as a market order (Price 0); StopLimit carries a limit Price. Both need a StopPrice.
        (IsStopMarketOrder && SlippagePercent is null && Price == 0m && StopPrice > 0m) ||
        (IsStopLimitOrder && SlippagePercent is null && Price > 0m && StopPrice > 0m);

    private bool IsValidBuyBudget()
    {
        // StopMarketBuy promotes to TrueMarketBuy, so it carries a budget like one.
        if ((IsTrueMarketOrder || IsStopMarketOrder) && IsBuyOrder)
            return BuyBudget.HasValue && BuyBudget.Value > 0m;
        return BuyBudget is null;
    }

    public override string ToString() =>
        $"Order #{OrderId} - {OrderType} - {Quantity} @ {PriceDisplay} - Status: {Status}";

    public string PriceDisplay
    {
        get
        {
            if (IsStopOrder)
            {
                var stop = CurrencyHelper.Format(StopPrice ?? 0m, CurrencyType);
                return IsStopLimitOrder
                    ? $"stop {stop} → {CurrencyHelper.Format(Price, CurrencyType)}"
                    : $"stop {stop} → MKT";
            }
            if (IsLimitOrder) return CurrencyHelper.Format(Price, CurrencyType);
            if (IsTrueMarketOrder) return "MARKET";
            var dir = IsBuyOrder ? "≤" : "≥";
            var cap = CurrencyHelper.Format(PriceWithSlippage, CurrencyType);
            return $"{dir} {cap}";
        }
    }
    public string TotalAmountDisplay
    {
        get
        {
            if (IsLimitOrder) return CurrencyHelper.Format(TotalAmount, CurrencyType);
            if (IsTrueMarketOrder) return "-";
            var dir = IsBuyOrder ? "≤" : "≥";
            var cap = CurrencyHelper.Format(TotalAmount, CurrencyType);
            return $"{dir} {cap}";
        }
    }

    public string AnchorPriceDisplay =>
        IsSlippageOrder ? CurrencyHelper.Format(Price, CurrencyType) : string.Empty;
    public string AmountFilledDisplay => $"{AmountFilled}/{Quantity}";
    public string BuyBudgetDisplay =>
        BuyBudget.HasValue ? CurrencyHelper.Format(BuyBudget.Value, CurrencyType) : "—";
    public string SlippageDisplay => SlippagePercent.HasValue ? $"±{SlippagePercent.Value:0.##}%" : "—";
    public string StopPriceDisplay =>
        StopPrice.HasValue ? CurrencyHelper.Format(StopPrice.Value, CurrencyType) : "—";

    public string SideDisplay => IsBuyOrder ? "BUY" : "SELL";
    public string TypeDisplay =>
        IsStopLimitOrder ? "STOP-LIM"
        : IsStopMarketOrder ? "STOP"
        : IsLimitOrder ? "LIMIT"
        : IsTrueMarketOrder ? "MKT" : "MKT±";
    public string SideTypeDisplay => $"{SideDisplay} {TypeDisplay}";
    public string StatusDisplay => Status.ToUpperInvariant();

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string CreatedDateShort => CreatedAt.ToLocalTime().ToString("dd-MM HH:mm");
    public string UpdatedAtDisplay => UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string UpdatedDateShort => UpdatedAt.ToLocalTime().ToString("dd-MM HH:mm");

    public bool IsBuyOrder => OrderType is Types.TrueMarketBuy or Types.SlippageMarketBuy or Types.LimitBuy
        or Types.StopMarketBuy or Types.StopLimitBuy;
    public bool IsSellOrder => OrderType is Types.TrueMarketSell or Types.SlippageMarketSell or Types.LimitSell
        or Types.StopMarketSell or Types.StopLimitSell;
    public bool IsLimitOrder =>
        OrderType == Types.LimitBuy || OrderType == Types.LimitSell;
    public bool IsMarketOrder => IsSlippageOrder || IsTrueMarketOrder;
    public bool IsSlippageOrder =>
        OrderType == Types.SlippageMarketBuy || OrderType == Types.SlippageMarketSell;
    public bool IsTrueMarketOrder => IsTrueMarketBuyOrder || OrderType == Types.TrueMarketSell;
    public bool IsTrueMarketBuyOrder => OrderType == Types.TrueMarketBuy;
    // §3.6 P2 stop helpers. A stop is neither a limit nor a market order while armed —
    // it carries no book presence until promoted to its StopPromotionTarget.
    public bool IsStopOrder => OrderType is Types.StopMarketBuy or Types.StopMarketSell
        or Types.StopLimitBuy or Types.StopLimitSell;
    public bool IsStopMarketOrder => OrderType is Types.StopMarketBuy or Types.StopMarketSell;
    public bool IsStopLimitOrder => OrderType is Types.StopLimitBuy or Types.StopLimitSell;
    public bool IsOpen => Status == Statuses.Open;
    public bool IsArmed => Status == Statuses.Pending;
    public bool IsFilled => Status == Statuses.Filled;
    public bool IsCancelled => Status == Statuses.Cancelled;
    // Terminal only — Pending (armed) is NOT closed, so retention/registry cleanup leave it be.
    public bool IsClosed => IsFilled || IsCancelled;
    public bool IsOpenLimitOrder => IsOpen && IsLimitOrder;

    // §3.6 P2: the active type an armed stop becomes when the watcher promotes it.
    public string StopPromotionTarget => OrderType switch
    {
        Types.StopMarketBuy  => Types.TrueMarketBuy,
        Types.StopMarketSell => Types.TrueMarketSell,
        Types.StopLimitBuy   => Types.LimitBuy,
        Types.StopLimitSell  => Types.LimitSell,
        _ => OrderType
    };
    public decimal TotalAmount => IsLimitOrder
        ? CurrencyHelper.Notional(Price, Quantity, CurrencyType)
        : (IsSlippageOrder && SlippagePercent.HasValue)
            ? CurrencyHelper.Notional(PriceWithSlippage!.Value, Quantity, CurrencyType)
            : 0m;
    public int RemainingQuantity => Quantity - AmountFilled;
    public decimal RemainingAmount => IsLimitOrder
        ? CurrencyHelper.Notional(Price, RemainingQuantity, CurrencyType)
        : (IsSlippageOrder && SlippagePercent.HasValue)
            ? CurrencyHelper.Notional(PriceWithSlippage!.Value, RemainingQuantity, CurrencyType)
            : 0m;
    public decimal FilledAmount => IsLimitOrder
        ? CurrencyHelper.Notional(Price, AmountFilled, CurrencyType)
        : (IsSlippageOrder && SlippagePercent.HasValue)
            ? CurrencyHelper.Notional(PriceWithSlippage!.Value, AmountFilled, CurrencyType)
            : 0m;
    public decimal? PriceWithSlippage => SlippagePercent.HasValue
        ? CurrencyHelper.ApplySlippagePct(Price, SlippagePercent.Value, IsBuyOrder, CurrencyType)
        : null;
    public decimal? EffectiveTakerLimit => IsSlippageOrder ? PriceWithSlippage : (IsLimitOrder ? Price : null);

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
        // An armed (Pending) stop is cancellable too — the user can pull it before it triggers.
        if (!IsOpen && !IsArmed) throw new InvalidOperationException("Only open or armed orders can be cancelled.");
        Status = Statuses.Cancelled;
        UpdatedAt = TimeHelper.NowUtc();
    }

    // §3.6 P2: place a stop in the armed (off-book) state.
    public void Arm()
    {
        if (!IsStopOrder) throw new InvalidOperationException("Only stop orders can be armed.");
        Status = Statuses.Pending;
        UpdatedAt = TimeHelper.NowUtc();
    }

    // §3.6 P2: the trigger watcher promotes an armed stop to its active type and opens it for
    // matching. StopPrice is kept for history; OrderType flips so the engine treats it normally.
    public void PromoteStop()
    {
        if (!IsArmed) throw new InvalidOperationException("Only an armed (Pending) stop can be promoted.");
        OrderType = StopPromotionTarget;
        Status = Statuses.Open;
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

    public Order Clone()
    {
        var clone = new Order
        {
            UserId = this.UserId,
            StockId = this.StockId,
            Quantity = this.Quantity,
            Price = this.Price,
            SlippagePercent = this.SlippagePercent,
            StopPrice = this.StopPrice,
            BuyBudget = this.BuyBudget,
            CurrencyType = this.CurrencyType,
            OrderType = this.OrderType,
            Status = this.Status,
            AmountFilled = this.AmountFilled,
            CreatedAt = this.CreatedAt,
            UpdatedAt = this.UpdatedAt
        };
        clone.CurrentBuyReservation = this.CurrentBuyReservation;
        clone.CurrentSellReservedQty = this.CurrentSellReservedQty;
        return clone;
    }

    public Order CloneFull()
    {
        var clone = Clone();
        clone.OrderId = OrderId;
        return clone;
    }

    public void TakeBuyReservation(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("TakeBuyReservation amount cannot be negative.", nameof(amount));
        CurrentBuyReservation += amount;
    }

    public void ConsumeBuyReservation(decimal amount)
    {
        if (amount < 0m)
            throw new ArgumentException("ConsumeBuyReservation amount cannot be negative.", nameof(amount));
        if (amount > CurrentBuyReservation)
            throw new InvalidOperationException(
                $"ConsumeBuyReservation #{OrderId}: amount {amount} exceeds current {CurrentBuyReservation}.");
        CurrentBuyReservation -= amount;
    }

    public decimal ReleaseBuyReservation()
    {
        var released = CurrentBuyReservation;
        CurrentBuyReservation = 0m;
        return released;
    }

    public void TakeSellReservation(int qty)
    {
        if (qty < 0)
            throw new ArgumentException("TakeSellReservation qty cannot be negative.", nameof(qty));
        CurrentSellReservedQty += qty;
    }

    public void ConsumeSellReservation(int qty)
    {
        if (qty < 0)
            throw new ArgumentException("ConsumeSellReservation qty cannot be negative.", nameof(qty));
        if (qty > CurrentSellReservedQty)
            throw new InvalidOperationException(
                $"ConsumeSellReservation #{OrderId}: qty {qty} exceeds current {CurrentSellReservedQty}.");
        CurrentSellReservedQty -= qty;
    }

    public int ReleaseSellReservation()
    {
        var released = CurrentSellReservedQty;
        CurrentSellReservedQty = 0;
        return released;
    }

    /// <summary>
    /// Rollback hook: restore the per-order reservation fields to a pre-mutation snapshot
    /// captured by the settlement scope.
    /// </summary>
    public void RestoreReservationFromSnapshot(decimal buy, int sell)
    {
        if (buy < 0m) throw new ArgumentException(nameof(buy));
        if (sell < 0) throw new ArgumentException(nameof(sell));
        CurrentBuyReservation = buy;
        CurrentSellReservedQty = sell;
    }
}
