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
    public void Capped_sell_stop_is_valid_and_promotes_to_slippage_market()
    {
        // §3.6: a sell-stop with a slippage cap fires as a capped market sell.
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 10,
            Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop,
            Price = 100m, SlippagePercent = 0.5m, StopPrice = 90m, Status = Order.Statuses.Pending,
        };
        Assert.True(o.IsValid());
        Assert.True(o.IsStopOrder);
        Assert.True(o.IsStopMarketOrder);

        o.PromoteStop();
        Assert.False(o.IsStopOrder);
        Assert.True(o.IsSlippageOrder); // capped market sell after the trigger
        Assert.True(o.IsValid());
    }

    [Fact]
    public void Clone_preserves_stop_price()
    {
        var o = ArmedSellStop();
        var clone = o.Clone();
        Assert.Equal(90m, clone.StopPrice);
        Assert.Equal(Order.Types.StopMarketSell, clone.OrderType);
    }

    // F1 (ORDER_TEST_FINDINGS): "limit-trigger shows 'market' instead of 'limit' after promotion."
    // Pins the display layer — what the open-orders / history table actually shows in the Type
    // column — across all four arm/promote permutations so a future regression would land here
    // before the manual repro could even be set up.
    [Theory]
    [InlineData(OrderSide.Buy,  EntryType.Limit,  "STOP-LIM", "LIMIT")]
    [InlineData(OrderSide.Sell, EntryType.Limit,  "STOP-LIM", "LIMIT")]
    [InlineData(OrderSide.Buy,  EntryType.Market, "STOP",     "MKT")]
    [InlineData(OrderSide.Sell, EntryType.Market, "STOP",     "MKT")]
    public void Promotion_preserves_Entry_kind_in_TypeDisplay(OrderSide side, EntryType entry,
        string armedDisplay, string promotedDisplay)
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 5,
            // A limit carries Price (its limit), a market carries 0 + (buy: budget; sell: nothing).
            Price      = entry == EntryType.Limit ? 105m : 0m,
            BuyBudget  = entry == EntryType.Market && side == OrderSide.Buy ? 550m : null,
            StopPrice  = side == OrderSide.Buy ? 104m : 90m,
            Side = side, Entry = entry, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        };
        Assert.True(o.IsValid());
        Assert.Equal(armedDisplay, o.TypeDisplay);

        o.PromoteStop();
        Assert.True(o.IsOpen);
        Assert.Equal(promotedDisplay, o.TypeDisplay);
    }
}
