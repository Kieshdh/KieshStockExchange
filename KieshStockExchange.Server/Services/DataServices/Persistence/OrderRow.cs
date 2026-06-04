using SQLite;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices.Persistence;

[Table("Orders")]
public class OrderRow
{
    [PrimaryKey, AutoIncrement]
    [Column("OrderId")] public int OrderId { get; set; }

    [Indexed(Name = "IX_Orders_User_Status", Order = 1)]
    [Column("UserId")] public int UserId { get; set; }

    [Indexed(Name = "IX_Orders_Stock_Status", Order = 1)]
    [Column("StockId")] public int StockId { get; set; }

    [Column("Quantity")] public int Quantity { get; set; }

    [Column("Price")] public decimal Price { get; set; }

    [Column("SlippagePercent")] public decimal? SlippagePercent { get; set; }

    [Column("BuyBudget")] public decimal? BuyBudget { get; set; }

    // §3.6 P2: trigger level for stop orders (null for non-stops).
    [Column("StopPrice")] public decimal? StopPrice { get; set; }

    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    [Column("OrderType")] public string OrderType { get; set; } = string.Empty;

    // Two overlapping composite indexes intentionally — both terminate on Status.
    [Indexed(Name = "IX_Orders_Stock_Status", Order = 2)]
    [Indexed(Name = "IX_Orders_User_Status", Order = 2)]
    [Column("Status")] public string Status { get; set; } = Order.Statuses.Open;

    [Column("AmountFilled")] public int AmountFilled { get; set; }

    [Column("CreatedAt")] public DateTime CreatedAt { get; set; }
    [Column("UpdatedAt")] public DateTime UpdatedAt { get; set; }
}

public static class OrderMapper
{
    public static Order ToDomain(OrderRow r) => new()
    {
        OrderId = r.OrderId,
        UserId = r.UserId,
        StockId = r.StockId,
        Quantity = r.Quantity,
        Price = r.Price,
        SlippagePercent = r.SlippagePercent,
        BuyBudget = r.BuyBudget,
        StopPrice = r.StopPrice,
        Currency = r.Currency,
        OrderType = r.OrderType,
        Status = r.Status,
        AmountFilled = r.AmountFilled,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    public static OrderRow ToRow(Order o) => new()
    {
        OrderId = o.OrderId,
        UserId = o.UserId,
        StockId = o.StockId,
        Quantity = o.Quantity,
        Price = o.Price,
        SlippagePercent = o.SlippagePercent,
        BuyBudget = o.BuyBudget,
        StopPrice = o.StopPrice,
        Currency = o.Currency,
        OrderType = o.OrderType,
        Status = o.Status,
        AmountFilled = o.AmountFilled,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt,
    };
}
