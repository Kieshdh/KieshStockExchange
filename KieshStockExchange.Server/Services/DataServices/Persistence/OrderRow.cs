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

    // §3.6 P3 trailing schema (behavior deferred): null unless Stop == Trailing.
    [Column("TrailOffset")] public decimal? TrailOffset { get; set; }
    [Column("TrailIsPercent")] public bool? TrailIsPercent { get; set; }
    [Column("TrailWatermark")] public decimal? TrailWatermark { get; set; }

    // §3.6 P4: a bracket child points at its parent entry order (null for parents/standalone).
    [Column("ParentOrderId")] public int? ParentOrderId { get; set; }

    [Column("Currency")] public string Currency { get; set; } = nameof(CurrencyType.USD);

    // §3.6 decomposition: the type is three orthogonal string columns (the flat OrderType column
    // is gone; the domain exposes a computed read-only OrderType for legacy readers).
    [Column("Side")] public string Side { get; set; } = nameof(OrderSide.Buy);
    [Column("Entry")] public string Entry { get; set; } = nameof(EntryType.Limit);
    [Column("Stop")] public string Stop { get; set; } = nameof(StopKind.None);

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
    // §3.6 decomposition: map the three string columns ↔ the domain enums (parse defensively;
    // an unrecognized value falls back to the safe default rather than throwing on load).
    private static OrderSide ParseSide(string s) => Enum.TryParse<OrderSide>(s, out var v) ? v : OrderSide.Buy;
    private static EntryType ParseEntry(string s) => Enum.TryParse<EntryType>(s, out var v) ? v : EntryType.Limit;
    private static StopKind ParseStop(string s) => Enum.TryParse<StopKind>(s, out var v) ? v : StopKind.None;

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
        TrailOffset = r.TrailOffset,
        TrailIsPercent = r.TrailIsPercent,
        TrailWatermark = r.TrailWatermark,
        ParentOrderId = r.ParentOrderId,
        Currency = r.Currency,
        Side = ParseSide(r.Side),
        Entry = ParseEntry(r.Entry),
        Stop = ParseStop(r.Stop),
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
        TrailOffset = o.TrailOffset,
        TrailIsPercent = o.TrailIsPercent,
        TrailWatermark = o.TrailWatermark,
        ParentOrderId = o.ParentOrderId,
        Currency = o.Currency,
        Side = o.Side.ToString(),
        Entry = o.Entry.ToString(),
        Stop = o.Stop.ToString(),
        Status = o.Status,
        AmountFilled = o.AmountFilled,
        CreatedAt = o.CreatedAt,
        UpdatedAt = o.UpdatedAt,
    };
}
