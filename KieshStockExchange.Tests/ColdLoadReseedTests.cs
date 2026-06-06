using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 P4 cold-load (<see cref="AccountsCache.EnsureLoadedAsync"/>) regressions for the two defects
/// from the Batch-H review. Both are conservation-clean, so the reconciler can't catch them — the
/// oracles here are order status (Q1) and fund validity (Q2).
///   Q1 — an ACTIVE bracket's legs must survive a restart. ReseedBracketReservations rebuilds the
///        SL-owns-pool / TP-only model BEFORE the generic per-sell clamp, which otherwise cancels the
///        pooled TPs as over-reservers.
///   Q2 — ClampBuysToFundBalance runs LAST and caps buys against TotalBalance MINUS short collateral
///        already reserved (and adds, not overwrites), or buys + collateral can push ReservedBalance
///        past TotalBalance — an invalid Fund (Available &lt; 0).
/// </summary>
public class ColdLoadReseedTests
{
    private const int UserId = 7;
    private const int StockId = 10;
    private const int ParentId = 500;

    private static AccountsCache NewCache(
        List<Fund> funds, List<Position> positions, List<Order> openOrders)
    {
        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(funds);
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(positions);
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(openOrders);
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Returns(Task.CompletedTask);
        var registry = new OrderRegistry();
        var ledger = new Mock<IReservationLedger>(MockBehavior.Loose).Object;
        return new AccountsCache(db.Object, registry, ledger, NullLogger<AccountsCache>.Instance);
    }

    private static Fund UsdFund(decimal total) => new Fund
    {
        // DB may carry a stale ReservedBalance; cold-load clears it then rebuilds.
        UserId = UserId, CurrencyType = CurrencyType.USD, TotalBalance = total, ReservedBalance = 0m,
    };

    private static Order BracketSl(int orderId, int poolQty)
    {
        var o = new Order
        {
            UserId = UserId, StockId = StockId, Quantity = poolQty, Price = 0m, StopPrice = 50m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Market,
            Stop = StopKind.Stop, ParentOrderId = ParentId,
        };
        o.OrderId = orderId;
        o.Arm();                         // Pending; CSR stays 0 (cold-load reconstructs it)
        return o;                        // RemainingQuantity == poolQty (Quantity − AmountFilled)
    }

    private static Order BracketTp(int orderId, int qty, decimal price)
    {
        var o = new Order
        {
            UserId = UserId, StockId = StockId, Quantity = qty, Price = price,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Limit,
            Stop = StopKind.None, ParentOrderId = ParentId,
        };
        o.OrderId = orderId;
        o.Status = Order.Statuses.Open;  // an armed TP rests Open reserving 0 (Model B)
        return o;
    }

    private static Order LimitBuy(int orderId, int qty, decimal price)
    {
        var o = new Order
        {
            UserId = UserId, StockId = StockId, Quantity = qty, Price = price,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        o.OrderId = orderId;
        o.Status = Order.Statuses.Open;
        return o;
    }

    [Fact]
    public async Task ActiveBracket_ColdLoad_KeepsTpsOpen_AndReseedsSlPool()
    {
        // Buyer holds 4 from the filled entry; SL owns the pool (4), two TPs reserve 0 (Model B).
        var pos = new Position { UserId = UserId, StockId = StockId, Quantity = 4 };
        var sl = BracketSl(orderId: 501, poolQty: 4);
        var tp1 = BracketTp(orderId: 502, qty: 2, price: 150m);
        var tp2 = BracketTp(orderId: 503, qty: 2, price: 180m);

        var cache = NewCache(
            new List<Fund> { UsdFund(100_000m) },
            new List<Position> { pos },
            new List<Order> { sl, tp1, tp2 });

        await cache.EnsureLoadedAsync(UserId);

        // TPs survive (pre-fix they were cancelled as StaleSeller:OverReserve).
        Assert.Equal(Order.Statuses.Open, tp1.Status);
        Assert.Equal(Order.Statuses.Open, tp2.Status);
        Assert.Equal(Order.Statuses.Pending, sl.Status);
        // SL owns the whole pool; TPs reserve nothing; Σ CSR == Position.ReservedQuantity.
        Assert.Equal(4, sl.CurrentSellReservedQty);
        Assert.Equal(0, tp1.CurrentSellReservedQty);
        Assert.Equal(0, tp2.CurrentSellReservedQty);
        Assert.Equal(4, pos.ReservedQuantity);
    }

    [Fact]
    public async Task TpOnlyBracket_ColdLoad_ReseedsEachTpReservation()
    {
        // No SL: each armed TP owns its own reservation (held 4 = 2 + 2).
        var pos = new Position { UserId = UserId, StockId = StockId, Quantity = 4 };
        var tp1 = BracketTp(orderId: 502, qty: 2, price: 150m);
        var tp2 = BracketTp(orderId: 503, qty: 2, price: 180m);

        var cache = NewCache(
            new List<Fund> { UsdFund(100_000m) },
            new List<Position> { pos },
            new List<Order> { tp1, tp2 });

        await cache.EnsureLoadedAsync(UserId);

        Assert.Equal(Order.Statuses.Open, tp1.Status);
        Assert.Equal(Order.Statuses.Open, tp2.Status);
        Assert.Equal(2, tp1.CurrentSellReservedQty);
        Assert.Equal(2, tp2.CurrentSellReservedQty);
        Assert.Equal(4, pos.ReservedQuantity);
    }

    [Fact]
    public async Task BuyCap_AccountsForShortCollateral_FundStaysValid()
    {
        // Inconsistent seed the clamp exists to sanitize: a filled short holding 200 collateral plus an
        // open limit buy reserving 119.44, against a TotalBalance of only 250. buys + collateral =
        // 319.44 > 250 would make the Fund invalid (Available < 0). The buy can't fit the 50 headroom.
        var fund = UsdFund(250m);
        var shortPos = new Position { UserId = UserId, StockId = StockId, Quantity = -5 };
        shortPos.ShortCollateral = 200m;
        shortPos.ShortCollateralCurrency = CurrencyType.USD;
        var buy = LimitBuy(orderId: 600, qty: 2, price: 59.72m); // reserves 119.44

        var cache = NewCache(
            new List<Fund> { fund },
            new List<Position> { shortPos },
            new List<Order> { buy });

        await cache.EnsureLoadedAsync(UserId);

        Assert.Equal(200m, fund.ReservedBalance);          // collateral kept, buy dropped
        Assert.True(fund.AvailableBalance >= 0m);
        Assert.True(fund.IsValid());
        Assert.Equal(Order.Statuses.Cancelled, buy.Status);
    }

    [Fact]
    public async Task BuyCap_NormalAccount_HydratesUnchanged()
    {
        // Regression: no collateral → buys cap against full Total exactly as before.
        var fund = UsdFund(1_000m);
        var buy = LimitBuy(orderId: 601, qty: 2, price: 100m); // reserves 200

        var cache = NewCache(
            new List<Fund> { fund },
            new List<Position>(),
            new List<Order> { buy });

        await cache.EnsureLoadedAsync(UserId);

        Assert.Equal(200m, fund.ReservedBalance);
        Assert.Equal(200m, buy.CurrentBuyReservation);
        Assert.Equal(Order.Statuses.Open, buy.Status);
    }
}
