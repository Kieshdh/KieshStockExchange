using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models;

[Table("Portfolios")]
public class Portfolio : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("PortfolioId")] public int PortfolioId { get; set; }

    [Column("UserId")] public int UserId { get; set; }

    [Column("StockId")] public int StockId { get; set; }

    [Column("Quantity")] public int Quantity { get; set; }

    [Column("ReservedQuantity")] 
    public int ReservedQuantity { get; set; } = 0;

    public int RemainingQuantity => Quantity - ReservedQuantity;

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
    #endregion

    public Portfolio()
    {
        UpdatedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
        Quantity = 0;
        ReservedQuantity = 0;
    }

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && StockId > 0 && 
        Quantity >= 0 && ReservedQuantity >= 0 && RemainingQuantity >= 0;

    public override string ToString() =>
        $"Portfolio #{PortfolioId}: User #{UserId} - Stock {StockId} with Quantity {Quantity}";
    #endregion

    #region Helper Methods
    public void AddStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive.");
        Quantity += quantity;
        UpdatedAt = DateTime.UtcNow;
    }
    public void RemoveStock(int quantity)
    {
        if (quantity <= 0 || quantity > Quantity)
            throw new ArgumentException("Invalid quantity to remove.");
        Quantity -= quantity;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ReserveStock(int quantity)
    {
        if (quantity <= 0 || quantity > RemainingQuantity)
            throw new ArgumentException("Invalid quantity to reserve.");
        ReservedQuantity += quantity;
        UpdatedAt = DateTime.UtcNow;
    }
    public void UnreserveStock(int quantity)
    {
        if (quantity <= 0 || quantity > ReservedQuantity)
            throw new ArgumentException("Invalid quantity to unreserve.");
        ReservedQuantity -= quantity;
        UpdatedAt = DateTime.UtcNow;
    }
    #endregion

}
