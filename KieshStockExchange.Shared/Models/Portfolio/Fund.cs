using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

public class Fund : IValidatable
{
    private int _fundId = 0;
    public int FundId
    {
        get => _fundId;
        set
        {
            if (_fundId != 0 && value != _fundId) throw new InvalidOperationException("FundId is immutable once set.");
            _fundId = value < 0 ? 0 : value;
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

    private decimal _totalBalance = 0m;
    public decimal TotalBalance
    {
        get => _totalBalance;
        set => _totalBalance = value;
    }

    private decimal _reservedBalance = 0m;
    public decimal ReservedBalance
    {
        get => _reservedBalance;
        set => _reservedBalance = value;
    }

    public decimal AvailableBalance => TotalBalance - ReservedBalance;

    public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
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

    public bool IsValid() => UserId > 0 && IsValidBalances() && IsValidCurrency();

    public bool IsInvalid => !IsValid();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidBalances() =>
           (TotalBalance >= 0 || CurrencyHelper.IsEffectivelyZero(TotalBalance, CurrencyType))
        && (ReservedBalance >= 0 || CurrencyHelper.IsEffectivelyZero(ReservedBalance, CurrencyType))
        && (AvailableBalance >= 0 || CurrencyHelper.IsEffectivelyZero(AvailableBalance, CurrencyType));

    private bool IsValidTimestamps() => CreatedAt > DateTime.MinValue &&
        CreatedAt <= TimeHelper.NowUtc() && UpdatedAt >= CreatedAt;

    public override string ToString() =>
        $"Fund #{FundId}: User #{UserId} - Balance: {TotalBalanceDisplay} - Reserved {ReservedBalanceDisplay}";

    private string PriceString(decimal val) => CurrencyHelper.Format(val, CurrencyType);
    public string TotalBalanceDisplay => PriceString(TotalBalance);
    public string ReservedBalanceDisplay => PriceString(ReservedBalance);
    public string AvailableBalanceDisplay => PriceString(AvailableBalance);

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("dd/MM/yy HH:mm:ss");
    public string UpdatedAtDisplay => UpdatedAt.ToLocalTime().ToString("dd/MM/yy HH:mm:ss");

    public void AddFunds(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException($"{ToString()} - Amount must be positive.");
        TotalBalance += amount;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void WithdrawFunds(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException($"{ToString()} - Amount must be positive.");
        if (!CurrencyHelper.GreaterOrEqual(AvailableBalance, amount, CurrencyType))
            throw new ArgumentException($"{ToString()} - Insufficient available balance. " +
                $"Available={AvailableBalanceDisplay}. Amount={CurrencyHelper.Format(amount, CurrencyType)}.");
        TotalBalance -= amount;
        if (CurrencyHelper.IsEffectivelyZero(TotalBalance, CurrencyType))
            TotalBalance = 0m; // Avoid negative zero
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void ConsumeReservedFunds(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException($"{ToString()} - Amount must be positive.");
        if (!CurrencyHelper.GreaterOrEqual(ReservedBalance, amount, CurrencyType))
            throw new ArgumentException($"{ToString()} - Insufficient reserved balance. " +
                $"Reserved={ReservedBalanceDisplay}. Amount={CurrencyHelper.Format(amount, CurrencyType)}.");
        ReservedBalance -= amount;
        TotalBalance -= amount;
        if (CurrencyHelper.IsEffectivelyZero(TotalBalance, CurrencyType))
            TotalBalance = 0m;
        if (CurrencyHelper.IsEffectivelyZero(ReservedBalance, CurrencyType))
            ReservedBalance = 0m;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void ReserveFunds(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException($"{ToString()} - Amount must be positive.");
        if (!CurrencyHelper.GreaterOrEqual(AvailableBalance, amount, CurrencyType))
            throw new ArgumentException($"{ToString()} - Insufficient available balance. " +
                $"Available={AvailableBalanceDisplay}. Amount={CurrencyHelper.Format(amount, CurrencyType)}.");
        ReservedBalance += amount;
        UpdatedAt = TimeHelper.NowUtc();
    }

    public void UnreserveFunds(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException($"{ToString()} - Amount must be positive.");
        if (!CurrencyHelper.GreaterOrEqual(ReservedBalance, amount, CurrencyType))
            throw new ArgumentException($"{ToString()} - Insufficient reserved balance. " +
                $"Reserved={ReservedBalanceDisplay}. Amount={CurrencyHelper.Format(amount, CurrencyType)}.");
        ReservedBalance -= amount;
        if (CurrencyHelper.IsEffectivelyZero(ReservedBalance, CurrencyType))
            ReservedBalance = 0m;
        UpdatedAt = TimeHelper.NowUtc();
    }
}
