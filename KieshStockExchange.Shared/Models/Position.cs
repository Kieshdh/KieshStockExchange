using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Position : IValidatable
{
    private int _positionId = 0;
    public int PositionId
    {
        get => _positionId;
        set
        {
            if (_positionId != 0 && value != _positionId) throw new InvalidOperationException("PositionId is immutable once set.");
            _positionId = value < 0 ? 0 : value;
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

    private int _reservedQuantity = 0;
    public int ReservedQuantity
    {
        get => _reservedQuantity;
        set => _reservedQuantity = value;
    }

    public int AvailableQuantity => Quantity - ReservedQuantity;

    // Short support (§3.6 P1): a negative Quantity is a cash-collateralized short.
    // ShortCollateral is the cash locked on the owner's Fund (mirrored into
    // Fund.ReservedBalance) backing that short; it lives here, not on the order,
    // because it is opened by the short sell but discharged by a different
    // buy-to-close order and must outlive the opening order's fill.
    private decimal _shortCollateral = 0m;
    public decimal ShortCollateral
    {
        get => _shortCollateral;
        set => _shortCollateral = value;
    }

    // Currency the ShortCollateral cash is reserved in — needed to map the
    // collateral back to the right per-currency Fund (positions are otherwise
    // currency-agnostic). Meaningful only while ShortCollateral > 0.
    public CurrencyType ShortCollateralCurrency { get; set; } = CurrencyType.USD;
    public string ShortCollateralCurrencyCode
    {
        get => ShortCollateralCurrency.ToString();
        set => ShortCollateralCurrency = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    public bool IsShort => Quantity < 0;

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

    // Quantity may be negative (a short). ReservedQuantity is share-side and only
    // applies to a long balance, so it stays in [0, max(Quantity,0)]. ShortCollateral
    // is non-negative and only present while short (Quantity < 0). Mirrors the
    // CK_Positions_Quantity_Invariants check constraint in KseDbContext.
    public bool IsValid() => UserId > 0 && StockId > 0 &&
        ReservedQuantity >= 0 && ReservedQuantity <= Math.Max(Quantity, 0) &&
        ShortCollateral >= 0m && (Quantity < 0 || ShortCollateral == 0m);

    public bool IsInvalid => !IsValid();

    public override string ToString() =>
        $"Position #{PositionId}: User #{UserId} - Stock {StockId} - Qty {Quantity} (Reserved {ReservedQuantity}" +
        (IsShort ? $", ShortCollateral {ShortCollateral})" : ")");

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string UpdatedAtDisplay => UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    public void AddStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive.");
        Quantity += quantity;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void RemoveStock(int quantity)
    {
        if (quantity <= 0 || quantity > AvailableQuantity)
            throw new ArgumentException("Invalid quantity to remove.");
        Quantity -= quantity;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void ConsumeReservedStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive.");
        if (quantity > ReservedQuantity)
            throw new ArgumentException("Invalid reserved quantity");
        ReservedQuantity -= quantity;
        Quantity -= quantity;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void ReserveStock(int quantity)
    {
        if (quantity <= 0 || quantity > AvailableQuantity)
            throw new ArgumentException("Invalid quantity to reserve.");
        ReservedQuantity += quantity;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void UnreserveStock(int quantity)
    {
        if (quantity <= 0 || quantity > ReservedQuantity)
            throw new ArgumentException("Invalid quantity to unreserve.");
        ReservedQuantity -= quantity;
        UpdatedAt = TimeHelper.NowUtc();
    }

    // Signed adjustment used by the short paths: opening a short pushes Quantity
    // negative (delta < 0); a buy-to-close on the buyer side already uses Quantity +=.
    // Guarded so a short can never coexist with a share reservation.
    public void ApplyDelta(int signedQty)
    {
        if (signedQty == 0)
            throw new ArgumentException("Delta must be non-zero.");
        Quantity += signedQty;
        if (Quantity < 0 && ReservedQuantity != 0)
            throw new InvalidOperationException("A short position cannot hold a share reservation.");
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void TakeShortCollateral(decimal amount, CurrencyType currency)
    {
        if (amount <= 0m)
            throw new ArgumentException("Collateral amount must be positive.");
        if (ShortCollateral > 0m && currency != ShortCollateralCurrency)
            throw new InvalidOperationException(
                $"Short collateral already held in {ShortCollateralCurrency}; cannot add {currency}.");
        ShortCollateral += amount;
        ShortCollateralCurrency = currency;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void ReleaseShortCollateral(decimal amount)
    {
        if (amount <= 0m)
            throw new ArgumentException("Collateral amount must be positive.");
        if (amount > ShortCollateral)
            throw new ArgumentException(
                $"Cannot release {amount}; only {ShortCollateral} collateral held.");
        ShortCollateral -= amount;
        if (CurrencyHelper.IsEffectivelyZero(ShortCollateral, ShortCollateralCurrency))
            ShortCollateral = 0m; // avoid a negative-zero residue
        UpdatedAt = TimeHelper.NowUtc();
    }
}
