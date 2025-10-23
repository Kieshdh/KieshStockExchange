using SQLite;
using KieshStockExchange.Helpers;
using System.Diagnostics;

namespace KieshStockExchange.Models;

[Table("Funds")]
public class Fund : IValidatable
{
    #region Properties
    private int _fundId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("FundId")] public int FundId 
    { 
        get => _fundId; 
        set {
            if (_fundId != 0 && value != _fundId) throw new InvalidOperationException("FundId is immutable once set.");
            _fundId = value < 0 ? 0 : value;
        }
    }

    private int _userId = 0;
    [Indexed(Name = "IX_Funds_User_Currency", Order = 1, Unique = true)]
    [Column("UserId")] public int UserId { 
        get => _userId; 
        set {
            if (_userId != 0 && value != _userId) throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    private decimal _totalBalance = 0m;
    [Column("TotalBalance")] public decimal TotalBalance { 
        get => _totalBalance; 
        set => _totalBalance = value; 
    }

    private decimal _reservedBalance = 0m;
    [Column("ReservedBalance")] public decimal ReservedBalance { 
        get => _reservedBalance; 
        set => _reservedBalance = value;
    }

    [Ignore] public decimal AvailableBalance => TotalBalance - ReservedBalance;

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Indexed(Name = "IX_Funds_User_Currency", Order = 2, Unique = true)]
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
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
    public bool IsValid() => UserId > 0 && IsValidBalances() && IsValidCurrency();

    public bool IsInvalid => !IsValid();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidBalances() =>
           (TotalBalance >= 0 || CurrencyHelper.IsEffectivelyZero(TotalBalance, CurrencyType)) 
        && (ReservedBalance >= 0 || CurrencyHelper.IsEffectivelyZero(ReservedBalance, CurrencyType))
        && (AvailableBalance >= 0 || CurrencyHelper.IsEffectivelyZero(AvailableBalance, CurrencyType));

    private bool IsValidTimestamps() => CreatedAt > DateTime.MinValue &&
        CreatedAt <= TimeHelper.NowUtc() && UpdatedAt >= CreatedAt;
    #endregion

    #region String Representation
    public override string ToString() =>
        $"Fund #{FundId}: User #{UserId} - Balance: {TotalBalanceDisplay} - Reserved {ReservedBalanceDisplay}";

    private string PriceString(decimal val) => CurrencyHelper.Format(val, CurrencyType);
    [Ignore] public string TotalBalanceDisplay => PriceString(TotalBalance);
    [Ignore] public string ReservedBalanceDisplay => PriceString(ReservedBalance);
    [Ignore] public string AvailableBalanceDisplay => PriceString(AvailableBalance);

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    #endregion

    #region Helper Methods
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
            TotalBalance = 0m; // Avoid negative zero
        if (CurrencyHelper.IsEffectivelyZero(ReservedBalance, CurrencyType))
            ReservedBalance = 0m; // Avoid negative zero
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
            ReservedBalance = 0m; // Avoid negative zero
        UpdatedAt = TimeHelper.NowUtc();
    }
    #endregion
}
