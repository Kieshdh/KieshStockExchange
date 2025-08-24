using SQLite;

namespace KieshStockExchange.Models;

[Table("Funds")]
public class Fund : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("FundId")] public int FundId { get; set; }

    [Column("UserId")] public int UserId { get; set; }

    [Column("TotalBalance")] 
    public decimal TotalBalance { get; set; }
    
    [Column("ReservedBalance")]  
    public decimal ReservedBalance { get; set; }

    [Ignore] 
    public decimal AvailableBalance => TotalBalance - ReservedBalance;

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
    #endregion

    public Fund()
    {
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        TotalBalance = 0;
        ReservedBalance = 0;
    }

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && TotalBalance >= 0
        && ReservedBalance >= 0 && AvailableBalance >= 0;
    #endregion

    public override string ToString() =>
        $"Fund #{FundId}: User #{UserId} - Balance: {Math.Round(TotalBalance, 2)}, ";

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
