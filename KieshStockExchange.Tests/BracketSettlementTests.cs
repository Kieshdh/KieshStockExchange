using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 P4. The bracket TP holds no reservation of its own — its shares are reserved on the Position
/// by the sibling SL (the shared pool). SellerCapacityValidator must accept a TP fill by drawing from
/// that per-position pool, and must reject once the pool is exhausted so two TP fills in one batch
/// can't oversell the held position (risk register #2). Plus model invariants for bracket children.
/// </summary>
public class BracketSettlementTests
{
    private const int Seller = 1;
    private const int Buyer = 2;
    private const int StockId = 10;

    private static AccountsCache CacheWithReservedPosition(int reserved, int quantity)
    {
        var db = new Mock<IDataBaseService>(MockBehavior.Loose).Object;
        var ledger = new Mock<IReservationLedger>(MockBehavior.Loose).Object;
        var cache = new AccountsCache(db, new OrderRegistry(), ledger, NullLogger<AccountsCache>.Instance);
        var pos = new Position { UserId = Seller, StockId = StockId, Quantity = quantity };
        if (reserved > 0) pos.ReserveStock(reserved);
        cache.TrackNewPosition(pos);
        return cache;
    }

    private static Order BracketTp(int orderId)
    {
        var tp = new Order
        {
            UserId = Seller, StockId = StockId, Quantity = 10, Price = 60m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        tp.OrderId = orderId;
        tp.ParentOrderId = 999; // marks it a bracket child; reserves nothing of its own
        return tp;
    }

    private static Transaction Fill(int sellOrderId, int buyOrderId, int qty) => new()
    {
        StockId = StockId, BuyOrderId = buyOrderId, SellOrderId = sellOrderId,
        BuyerId = Buyer, SellerId = Seller, Quantity = qty, Price = 60m, CurrencyType = CurrencyType.USD,
    };

    [Fact]
    public void BracketTp_DrawsFromPosPool_AcceptsUpToHeld_RejectsOverDraw()
    {
        var validator = new SellerCapacityValidator(NullLogger<SellerCapacityValidator>.Instance);
        var cache = CacheWithReservedPosition(reserved: 10, quantity: 10); // SL holds all 10
        var tp = BracketTp(orderId: 200);
        var ordersById = new Dictionary<int, Order> { [200] = tp };

        // Two fills against the resting TP in one batch: 4 (fits the pool) then 7 (overdraws 6 left).
        var trades = new List<Transaction> { Fill(200, 300, 4), Fill(200, 301, 7) };

        var (err, accepted, rejected) = validator.Filter(
            trades, ordersById, cache, new Dictionary<(int, int), Position>(), CancellationToken.None);

        Assert.Null(err);
        Assert.Single(accepted);
        Assert.Equal(4, accepted[0].Quantity);
        Assert.Single(rejected);
        Assert.Equal(7, rejected[0].Trade.Quantity);
    }

    [Fact]
    public void BracketTp_WithNoReservedPool_IsRejected()
    {
        var validator = new SellerCapacityValidator(NullLogger<SellerCapacityValidator>.Instance);
        var cache = CacheWithReservedPosition(reserved: 0, quantity: 10); // SL never armed → empty pool
        var tp = BracketTp(orderId: 200);
        var ordersById = new Dictionary<int, Order> { [200] = tp };
        var trades = new List<Transaction> { Fill(200, 300, 3) };

        var (err, accepted, rejected) = validator.Filter(
            trades, ordersById, cache, new Dictionary<(int, int), Position>(), CancellationToken.None);

        Assert.Null(err);
        Assert.Empty(accepted);
        Assert.Single(rejected);
    }

    [Fact]
    public void AttachedBracketChildren_AreValid_AndIdentifiable()
    {
        // Dormant SL (stop-market sell).
        var sl = new Order
        {
            UserId = Seller, StockId = StockId, Quantity = 10, Price = 0m, StopPrice = 50m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop,
            ParentOrderId = 999, Status = Order.Statuses.Attached,
        };
        Assert.True(sl.IsValid());
        Assert.True(sl.IsAttached);
        Assert.True(sl.IsBracketChild);
        Assert.False(sl.IsClosed); // Attached is never pruned

        // Dormant TP (limit sell).
        var tp = new Order
        {
            UserId = Seller, StockId = StockId, Quantity = 3, Price = 70m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
            ParentOrderId = 999, Status = Order.Statuses.Attached,
        };
        Assert.True(tp.IsValid());
        Assert.True(tp.IsAttached);
        Assert.True(tp.IsBracketChild);

        // ParentOrderId survives a clone.
        var clone = tp.Clone();
        Assert.Equal(999, clone.ParentOrderId);
    }
}
