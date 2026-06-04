using KieshStockExchange.Models;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 Patch 2 — Order state-machine invariants for stop orders (model-level, no engine/DB).
/// Direction sanity + reservation live in the server engine; these lock the domain behavior the
/// arm/promote/cancel paths rely on.
/// </summary>
public class StopOrderModelTests
{
    private static Order ArmedSellStop() => new()
    {
        UserId = 1, StockId = 1, Quantity = 10,
        Price = 0m, StopPrice = 90m,
        Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
    };

    [Fact]
    public void Armed_stop_is_valid_and_neither_open_nor_closed()
    {
        var o = ArmedSellStop();
        Assert.True(o.IsValid());
        Assert.True(o.IsArmed);
        Assert.True(o.IsStopOrder);
        Assert.False(o.IsOpen);
        Assert.False(o.IsClosed); // Pending must not count as closed (retention/registry rely on this)
    }

    [Fact]
    public void StopMarketSell_promotes_to_TrueMarketSell_and_opens()
    {
        var o = ArmedSellStop();
        o.PromoteStop();
        Assert.Equal(Order.Types.TrueMarketSell, o.OrderType);
        Assert.True(o.IsOpen);
        Assert.True(o.IsTrueMarketOrder);
        Assert.True(o.IsValid());
    }

    [Fact]
    public void StopLimitBuy_promotes_to_LimitBuy()
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 5,
            Price = 105m, StopPrice = 104m,
            Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        };
        Assert.True(o.IsValid());
        o.PromoteStop();
        Assert.Equal(Order.Types.LimitBuy, o.OrderType);
        Assert.True(o.IsOpen);
        Assert.True(o.IsLimitOrder);
        Assert.True(o.IsValid());
    }

    [Fact]
    public void StopMarketBuy_requires_a_positive_budget()
    {
        var withBudget = new Order
        {
            UserId = 1, StockId = 1, Quantity = 5,
            Price = 0m, StopPrice = 110m, BuyBudget = 550m,
            Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        };
        Assert.True(withBudget.IsValid());

        var noBudget = new Order
        {
            UserId = 1, StockId = 1, Quantity = 5,
            Price = 0m, StopPrice = 110m,
            Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        };
        Assert.False(noBudget.IsValid());
    }

    [Fact]
    public void StopMarket_with_nonzero_price_is_invalid()
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 5,
            Price = 10m, StopPrice = 90m, // StopMarket must have Price == 0
            Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        };
        Assert.False(o.IsValid());
    }

    [Fact]
    public void Stop_without_stop_price_is_invalid()
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 5,
            Price = 0m, // no StopPrice
            Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        };
        Assert.False(o.IsValid());
    }

    [Fact]
    public void Armed_stop_can_be_cancelled()
    {
        var o = ArmedSellStop();
        o.Cancel();
        Assert.True(o.IsCancelled);
        Assert.True(o.IsClosed);
    }

    [Fact]
    public void PromoteStop_only_valid_from_armed()
    {
        var o = ArmedSellStop();
        o.PromoteStop();
        Assert.Throws<InvalidOperationException>(() => o.PromoteStop()); // already Open
    }

    [Fact]
    public void Clone_preserves_stop_price()
    {
        var o = ArmedSellStop();
        var clone = o.Clone();
        Assert.Equal(90m, clone.StopPrice);
        Assert.Equal(Order.Types.StopMarketSell, clone.OrderType);
    }
}
