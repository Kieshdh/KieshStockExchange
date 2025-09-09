using SQLite;
using KieshStockExchange.Helpers;

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
    #endregion

    #region Properties
    [PrimaryKey, AutoIncrement]
    [Column("OrderId")] public int OrderId { get; set; } = 0;

    [Column("UserId")] public int UserId { get; set; } = 0;

    [Column("StockId")] public int StockId { get; set; } = 0;

    [Column("Quantity")] public int Quantity { get; set; } = 0;

    [Column("Price")] public decimal Price { get; set; } = 0;

    [Ignore] public decimal TotalAmount => Price * Quantity;

    [Ignore] public CurrencyType CurrencyType { get; set; } = CurrencyType.USD;
    [Column("Currency")] public string Currency
    {
        get => CurrencyType.ToString();
        set => CurrencyType = CurrencyHelper.FromIsoCodeOrDefault(value);
    }

    // "MarketBuy", "MarketSell", "LimitBuy", "LimitSell"
    [Column("OrderType")] public string OrderType { get; set; } = String.Empty;

    // "Open", "Filled", "Cancelled"
    [Column("Status")] public string Status { get; set; } = Statuses.Open;

    [Column("AmountFilled")] public int AmountFilled { get; set; } = 0;

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    #endregion

    #region IValidatable Implementation
    public bool IsValid() => UserId > 0 && StockId > 0 && Quantity > 0 && Price > 0 &&
        IsValidOrderType() && IsValidStatus() && IsValidCurrency() && IsValidAmount();

    private bool IsValidOrderType() =>
        OrderType == Types.MarketBuy || OrderType == Types.MarketSell ||
        OrderType == Types.LimitBuy || OrderType == Types.LimitSell;
    private bool IsValidStatus() =>
        Status == Statuses.Open || Status == Statuses.Filled || Status == Statuses.Cancelled;
    private bool IsValidCurrency() => CurrencyHelper.IsSupported(Currency);

    private bool IsValidAmount() =>
        (IsFilled() && AmountFilled == Quantity) || 
        (IsOpen() && RemainingQuantity() > 0) || 
        (IsCancelled() && AmountFilled != Quantity);

    #endregion

    #region String Representations
    public override string ToString() =>
        $"Order #{OrderId} - {OrderType} {Quantity} @ {PriceDisplay} - Status: {Status}";
    [Ignore] public string PriceDisplay => CurrencyHelper.Format(Price, CurrencyType);
    [Ignore] public string TotalAmountDisplay => CurrencyHelper.Format(TotalAmount, CurrencyType);
    [Ignore] public string CreatedAtDisplay => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string UpdatedAtDisplay => UpdatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    [Ignore] public string AmountFilledDisplay => $"{AmountFilled}/{Quantity}";
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
