using System.Collections.Generic;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

// §perf C4 gate: AiBotDecisionService.ComputeCommitted replaced three per-call walks of OpenOrders
// (ComputeCommittedBuyFunds / ComputeCommittedSellShares / ComputeCommittedCoverShares) with ONE walk per
// decision. These tests pin that the single pass reproduces the old per-call totals exactly — same
// predicates, same buckets — so the decision path (which only consumes these totals) is output-identical.
public class CommittedTotalsTests
{
    private static AiBotContext NewContext()
        => new AiBotContext(new Mock<IAccountsCache>(MockBehavior.Loose).Object, personalSentiment: false);

    private static Order Limit(int orderId, int userId, int stockId, CurrencyType ccy,
        OrderSide side, int qty, decimal price) => new Order
    {
        OrderId = orderId, UserId = userId, StockId = stockId, CurrencyType = ccy,
        Side = side, Entry = EntryType.Limit, Stop = StopKind.None,
        Quantity = qty, Price = price,
    };

    // The old per-call helpers, re-derived inline as the oracle (byte-for-byte the predicates they used).
    private static decimal OldBuyFunds(AiBotContext ctx, int userId, CurrencyType ccy)
    {
        decimal c = 0m;
        if (ctx.OpenOrders.TryGetValue(userId, out var orders))
            foreach (var o in orders.Values)
                if (o.IsBuyOrder && o.IsLimitOrder && o.CurrencyType == ccy) c += o.RemainingAmount;
        return c;
    }
    private static int OldSellShares(AiBotContext ctx, int userId, int stockId)
    {
        int c = 0;
        if (ctx.OpenOrders.TryGetValue(userId, out var orders))
            foreach (var o in orders.Values)
                if (o.IsSellOrder && o.IsLimitOrder && o.StockId == stockId) c += o.RemainingQuantity;
        return c;
    }
    private static int OldCoverShares(AiBotContext ctx, int userId, int stockId)
    {
        int c = 0;
        if (ctx.OpenOrders.TryGetValue(userId, out var orders))
            foreach (var o in orders.Values)
                if (o.IsBuyOrder && o.IsLimitOrder && o.StockId == stockId) c += o.RemainingQuantity;
        return c;
    }

    [Fact]
    public void ComputeCommitted_matches_old_per_call_helpers_across_currencies_and_stocks()
    {
        const int user = 42;
        var ctx = NewContext();
        ctx.OpenOrders[user] = new Dictionary<int, Order>
        {
            // two buy limits, same stock+currency → must sum on both the currency (funds) and stock (cover) buckets
            [1] = Limit(1, user, stockId: 1, CurrencyType.USD, OrderSide.Buy,  qty: 10, price: 5m),
            [2] = Limit(2, user, stockId: 1, CurrencyType.USD, OrderSide.Buy,  qty: 4,  price: 6m),
            // buy limit in a different currency + stock
            [3] = Limit(3, user, stockId: 2, CurrencyType.EUR, OrderSide.Buy,  qty: 3,  price: 7m),
            // sell limits on two stocks
            [4] = Limit(4, user, stockId: 1, CurrencyType.USD, OrderSide.Sell, qty: 8,  price: 9m),
            [5] = Limit(5, user, stockId: 3, CurrencyType.USD, OrderSide.Sell, qty: 2,  price: 4m),
            // NON-limit orders must be excluded by every bucket
            [6] = new Order { OrderId = 6, UserId = user, StockId = 1, CurrencyType = CurrencyType.USD,
                              Side = OrderSide.Buy, Entry = EntryType.Market, Stop = StopKind.None, Quantity = 99, Price = 5m },
            [7] = new Order { OrderId = 7, UserId = user, StockId = 1, CurrencyType = CurrencyType.USD,
                              Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop, Quantity = 77, Price = 5m },
        };

        var got = AiBotDecisionService.ComputeCommitted(ctx, user);

        // Buy funds per currency
        Assert.Equal(OldBuyFunds(ctx, user, CurrencyType.USD), got.BuyFundsByCurrency.GetValueOrDefault(CurrencyType.USD));
        Assert.Equal(OldBuyFunds(ctx, user, CurrencyType.EUR), got.BuyFundsByCurrency.GetValueOrDefault(CurrencyType.EUR));
        // Sell shares per stock (stock 1 sells, stock 3 sells, stock 2 has none)
        Assert.Equal(OldSellShares(ctx, user, 1), got.SellSharesByStock.GetValueOrDefault(1));
        Assert.Equal(OldSellShares(ctx, user, 2), got.SellSharesByStock.GetValueOrDefault(2));
        Assert.Equal(OldSellShares(ctx, user, 3), got.SellSharesByStock.GetValueOrDefault(3));
        // Cover (buy) shares per stock
        Assert.Equal(OldCoverShares(ctx, user, 1), got.CoverSharesByStock.GetValueOrDefault(1));
        Assert.Equal(OldCoverShares(ctx, user, 2), got.CoverSharesByStock.GetValueOrDefault(2));

        // Explicit values, so a predicate regression can't pass by both sides drifting together.
        Assert.Equal(8, got.SellSharesByStock.GetValueOrDefault(1));   // only the sell limit, not the market sell-stop
        Assert.Equal(14, got.CoverSharesByStock.GetValueOrDefault(1)); // 10 + 4 buy limits, not the market buy
        Assert.Equal(3, got.CoverSharesByStock.GetValueOrDefault(2));
        Assert.False(got.SellSharesByStock.ContainsKey(2));            // no sell limit on stock 2
    }

    [Fact]
    public void ComputeCommitted_empty_when_user_has_no_open_orders()
    {
        var ctx = NewContext();
        var got = AiBotDecisionService.ComputeCommitted(ctx, userId: 999);
        Assert.Empty(got.BuyFundsByCurrency);
        Assert.Empty(got.SellSharesByStock);
        Assert.Empty(got.CoverSharesByStock);
        Assert.Equal(0m, got.BuyFundsByCurrency.GetValueOrDefault(CurrencyType.USD));
        Assert.Equal(0, got.SellSharesByStock.GetValueOrDefault(1));
    }
}
