using SQLite;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Models;

[Table("Positions")]
public class Position : IValidatable
{
    #region Properties
    private int _positionId = 0;
    [PrimaryKey, AutoIncrement]
    [Column("PositionId")] public int PositionId { 
        get => _positionId;
        set {
            if (_positionId != 0 && value != _positionId) throw new InvalidOperationException("PositionId is immutable once set.");
            _positionId = value < 0 ? 0 : value;
        }
    }

    private int _userId = 0;
    [Indexed(Name = "IX_Positions_User_Stock", Order = 1, Unique = true)]
    [Column("UserId")] public int UserId { 
        get => _userId; 
        set {
            if (_userId != 0 && value != _userId) throw new InvalidOperationException("UserId is immutable once set.");
            _userId = value;
        }
    }

    private int _stockId = 0;
    [Indexed(Name = "IX_Positions_User_Stock", Order = 2, Unique = true)]
    [Column("StockId")] public int StockId {
        get => _stockId; 
        set {
            if (_stockId != 0 && value != _stockId) throw new InvalidOperationException("StockId is immutable once set.");
            _stockId = value;
        }
    }

    private int _quantity = 0;
    [Column("Quantity")] public int Quantity { 
        get => _quantity; 
        set => _quantity = value;
    }

    private int _reservedQuantity = 0;
    [Column("ReservedQuantity")] public int ReservedQuantity { 
        get => _reservedQuantity; 
        set => _reservedQuantity = value;
    }

    [Ignore] public int AvailableQuantity => Quantity - ReservedQuantity;

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
    public bool IsValid() => UserId > 0 && StockId > 0 && 
        Quantity >= 0 && ReservedQuantity >= 0 && AvailableQuantity >= 0;

    public bool IsInvalid => !IsValid();
    #endregion

    #region String Representations
    public override string ToString() =>
        $"Position #{PositionId}: User #{UserId} - Stock {StockId} - Qty {Quantity} (Reserved {ReservedQuantity})";

    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    #endregion

    #region Helper Methods
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
    #endregion

}
