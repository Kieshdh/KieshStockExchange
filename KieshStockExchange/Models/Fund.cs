using SQLite;
using KieshStockExchange.Helpers;

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

    private decimal _totalBalance = 0;
    [Column("TotalBalance")] public decimal TotalBalance { 
        get => _totalBalance; 
        set => _totalBalance = value; 
    }

    private decimal _reservedBalance = 0;
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
    public bool IsValid() => UserId > 0 && TotalBalance >= 0
        && ReservedBalance >= 0 && AvailableBalance >= 0 && IsValidCurrency();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidBalances() =>
        TotalBalance >= 0 && ReservedBalance >= 0 && AvailableBalance >= 0;

    private bool IsValidTimestamps() => CreatedAt > DateTime.MinValue &&
        CreatedAt <= TimeHelper.NowUtc() && UpdatedAt >= CreatedAt;
    #endregion

    #region String Representation
    public override string ToString() =>
        $"Fund #{FundId}: User #{UserId} - Balance: {TotalBalanceDisplay}";

    private string priceString(decimal val) => CurrencyHelper.Format(val, CurrencyType);
    [Ignore] public string TotalBalanceDisplay => priceString(TotalBalance);
    [Ignore] public string ReservedBalanceDisplay => priceString(ReservedBalance);
    [Ignore] public string AvailableBalanceDisplay => priceString(AvailableBalance);

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    #endregion

    #region Helper Methods
    public void AddFunds(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive.");
        TotalBalance += amount;
        UpdatedAt = TimeHelper.NowUtc();
    }
    
    public void WithdrawFunds(decimal amount)
    {
        if (amount <= 0 || amount > AvailableBalance)
            throw new ArgumentException("Invalid amount to remove.");
        TotalBalance -= amount;
        UpdatedAt = TimeHelper.NowUtc();
    }
    
    public void ConsumeReservedFunds(decimal amount)
    {
        if (amount < 0) 
            throw new ArgumentException("Amount must be positive.");
        if (amount > ReservedBalance)
            throw new ArgumentException("Invalid reserved amount");
        ReservedBalance -= amount;
        TotalBalance -= amount;
        UpdatedAt = TimeHelper.NowUtc();
    }
    
    public void ReserveFunds(decimal amount)
    {
        if (amount <= 0 || amount > AvailableBalance)
            throw new ArgumentException("Invalid amount to reserve.");
        ReservedBalance += amount;
        UpdatedAt = TimeHelper.NowUtc();
    }
    
    public void UnreserveFunds(decimal amount)
    {
        if (amount <= 0 || amount > ReservedBalance)
            throw new ArgumentException("Invalid amount to release.");
        ReservedBalance -= amount;
        UpdatedAt = TimeHelper.NowUtc();
    }
    #endregion
}
