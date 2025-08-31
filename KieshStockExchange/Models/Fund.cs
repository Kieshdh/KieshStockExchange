using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Funds")]
public class Fund : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("FundId")] public int FundId { get; set; }

    [Column("UserId")] public int UserId { get; set; }

    [Column("TotalBalance")] public decimal TotalBalance { get; set; }
    
    [Column("ReservedBalance")] public decimal ReservedBalance { get; set; }

    [Ignore] public decimal AvailableBalance => TotalBalance - ReservedBalance;

    [Ignore] public CurrencyType CurrencyType { get; set; }
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
    #endregion

    #region Constructor
    public Fund()
    {
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        TotalBalance = 0;
        ReservedBalance = 0;
        CurrencyType = CurrencyType.USD; // Default currency
    }
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && TotalBalance >= 0
        && ReservedBalance >= 0 && AvailableBalance >= 0 && IsValidCurrency();

    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);
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
        UpdatedAt = DateTime.UtcNow;
    }
    public void RemoveFunds(decimal amount)
    {
        if (amount <= 0 || amount > TotalBalance)
            throw new ArgumentException("Invalid amount to remove.");
        TotalBalance -= amount;
        UpdatedAt = DateTime.UtcNow;
    }
    public void ReserveFunds(decimal amount)
    {
        if (amount <= 0 || amount > AvailableBalance)
            throw new ArgumentException("Invalid amount to reserve.");
        ReservedBalance += amount;
        UpdatedAt = DateTime.UtcNow;
    }
    public void UnreserveFunds(decimal amount)
    {
        if (amount <= 0 || amount > ReservedBalance)
            throw new ArgumentException("Invalid amount to release.");
        ReservedBalance -= amount;
        UpdatedAt = DateTime.UtcNow;
    }
    #endregion
}
