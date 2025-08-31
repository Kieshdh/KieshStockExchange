using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models;

[Table("Positions")]
public class Position : IValidatable
{
    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("PositionId")] public int PositionId { get; set; }

    [Column("UserId")] public int UserId { get; set; }

    [Column("StockId")] public int StockId { get; set; }

    [Column("Quantity")] public int Quantity { get; set; }

    [Column("ReservedQuantity")] public int ReservedQuantity { get; set; }

    [Ignore] public int RemainingQuantity => Quantity - ReservedQuantity;

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
    #endregion

    public Position()
    {
        UpdatedAt = DateTime.UtcNow;
        CreatedAt = DateTime.UtcNow;
        Quantity = 0;
        ReservedQuantity = 0;
    }

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && StockId > 0 && 
        Quantity >= 0 && ReservedQuantity >= 0 && RemainingQuantity >= 0;
    #endregion

    #region String Representations
    public override string ToString() =>
        $"Position #{PositionId}: User #{UserId} - Stock {StockId} | Qty {Quantity} (Reserved {ReservedQuantity})";

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToString("dd/MM/yyyy HH:mm:ss");
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
