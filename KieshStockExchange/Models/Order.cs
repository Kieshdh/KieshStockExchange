using SQLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KieshStockExchange.Models;

[Table("Orders")]
public class Order : IValidatable
{
    #region Constants
    public static class Types
    {
        public const string MarketBuy = "MarketBuy";
        public const string MarketSell = "MarketSell";
        public const string LimitBuy = "LimitBuy";
        public const string LimitSell = "LimitSell";
    }

    public static class Statuses
    {
        public const string Open = "Open";
        public const string Filled = "Filled";
        public const string Cancelled = "Cancelled";
    }

    public static class Currencies
    {
        public const string USD = "USD";
        public const string EUR = "EURO";
    }
    #endregion

    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("OrderId")] public int OrderId { get; set; }

    [Column("UserId")] public int UserId { get; set; }

    [Column("StockId")] public int StockId { get; set; }

    [Column("Quantity")] public int Quantity { get; set; }

    [Column("Price")] public decimal Price { get; set; }

    // "USD", "EUR"
    [Column("Currency")] public string Currency { get; set; }

    // "MarketBuy", "MarketSell", "LimitBuy", "LimitSell"
    [Column("OrderType")] public string OrderType { get; set; }

    // "Open", "Filled", "Cancelled"
    [Column("Status")] public string Status { get; set; }
    [Column("AmountFilled")] public int AmountFilled { get; set; }

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
    #endregion

    public Order()
    {
        CreatedAt = DateTime.UtcNow;
        Status = Statuses.Open; // Default status when order is created
        Currency = Currencies.USD; // Default currency
        AmountFilled = 0;
    }

    #region IValidatable Implementation
    public bool IsValid() => (UserId > 0 && StockId > 0 && Quantity > 0 && Price > 0 &&
                              IsValidOrderType() && IsValidStatus()) && IsValidCurrency();

    private bool IsValidOrderType() =>
        OrderType == Types.MarketBuy || OrderType == Types.MarketSell ||
        OrderType == Types.LimitBuy || OrderType == Types.LimitSell;
    private bool IsValidStatus() =>
        Status == Statuses.Open || Status == Statuses.Filled || Status == Statuses.Cancelled;
    private bool IsValidCurrency() =>
        Currency == Currencies.USD || Currency == Currencies.EUR;

    #endregion

    #region String Representations
    public override string ToString() =>
        $"Order #{OrderId} - {OrderType} {Quantity} @ {Price} - Status: {Status}";

    public string CreatedAtString() =>
        CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string UpdatedAtString() =>
        UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string PriceString() =>
        Currency switch
        {
            Currencies.USD => $"${Price:F2}",
            Currencies.EUR => $"€{Price:F2}",
            _ => $"{Price:F2} {Currency}"
        };
    #endregion

    #region Helper Methods
    public bool IsBuyOrder() =>
        OrderType == Types.MarketBuy || OrderType == Types.LimitBuy;
    public bool IsSellOrder() =>
        OrderType == Types.MarketSell || OrderType == Types.LimitSell;
    public bool IsLimitOrder() =>
        OrderType == Types.LimitBuy || OrderType == Types.LimitSell;
    public bool IsMarketOrder() =>
        OrderType == Types.MarketBuy || OrderType == Types.MarketSell;
    public bool IsOpen() => Status == Statuses.Open;
    public bool IsFilled() => Status == Statuses.Filled;
    public bool IsCancelled() => Status == Statuses.Cancelled;
    public int RemainingQuantity() => Quantity - AmountFilled;
    public decimal RemainingAmount() => RemainingQuantity() * Price;
    public void Fill(int quantity)
    {
        if (quantity <= 0 || quantity > RemainingQuantity())
            throw new ArgumentException("Invalid fill quantity.");
        AmountFilled += quantity;
        if (AmountFilled == Quantity)
            Status = Statuses.Filled;
        UpdatedAt = DateTime.UtcNow;
    }
    public void Cancel()
    {
        if (!IsOpen())
            throw new InvalidOperationException("Order is already cancelled.");
        Status = Statuses.Cancelled;
        UpdatedAt = DateTime.UtcNow;
    }
    public void UpdatePrice(decimal newPrice)
    {
        if (!IsOpen())
            throw new InvalidOperationException("Cannot update price of a non-open order.");
        if (newPrice <= 0)
            throw new ArgumentException("Price must be greater than zero.");
        Price = newPrice;
        UpdatedAt = DateTime.UtcNow;
    }
    public void UpdateQuantity(int newQuantity)
    {
        if (!IsOpen())
            throw new InvalidOperationException("Cannot update quantity of a non-open order.");
        if (newQuantity < AmountFilled)
            throw new ArgumentException("New quantity cannot be less than amount filled.");
        Quantity = newQuantity;
        UpdatedAt = DateTime.UtcNow;
        if (AmountFilled == Quantity)
            Status = Statuses.Filled; 
    }
    #endregion
}
