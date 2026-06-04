using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 risk #7 long→short flip. A MARKET sell by a long holder for more than the held shares must
/// be accepted by SellerCapacityValidator: the held portion closes the long, the excess opens a
/// collateral-backed short. (TradeSettler performs the per-fill split; these guard the accept gate.)
/// </summary>
public class FlipSettlementTests
{
    private const int Seller = 1;
    private const int Buyer = 2;
    private const int StockId = 10;

    private static AccountsCache CacheWithLong(int quantity)
    {
        var db = new Mock<IDataBaseService>(MockBehavior.Loose).Object;
        var ledger = new Mock<IReservationLedger>(MockBehavior.Loose).Object;
        var cache = new AccountsCache(db, new OrderRegistry(), ledger, NullLogger<AccountsCache>.Instance);
        cache.TrackNewPosition(new Position { UserId = Seller, StockId = StockId, Quantity = quantity });
        return cache;
    }

    private static Order MarketSell(int orderId, int qty, int reserved)
    {
        var o = new Order
        {
            UserId = Seller, StockId = StockId, Quantity = qty, Price = 0m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
        };
        o.OrderId = orderId;
        if (reserved > 0) o.TakeSellReservation(reserved); // place-time long reservation = held shares
        return o;
    }

    private static Transaction Fill(int sellOrderId, int buyOrderId, int qty) => new()
    {
        StockId = StockId, BuyOrderId = buyOrderId, SellOrderId = sellOrderId,
        BuyerId = Buyer, SellerId = Seller, Quantity = qty, Price = 50m, CurrencyType = CurrencyType.USD,
    };

    [Fact]
    public void Flip_market_sell_beyond_holdings_is_accepted()
    {
        var validator = new SellerCapacityValidator(NullLogger<SellerCapacityValidator>.Instance);
        var cache = CacheWithLong(quantity: 10);                     // holds 10 long
        var sell = MarketSell(orderId: 200, qty: 30, reserved: 10);  // sell 30 → flip short 20
        var ordersById = new Dictionary<int, Order> { [200] = sell };
        var empty = new Dictionary<(int, int), Position>();

        var (err, accepted, rejected) = validator.Filter(
            new[] { Fill(200, 300, 30) }, ordersById, cache, empty, default);

        Assert.Null(err);
        Assert.Single(accepted);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Flip_split_across_two_fills_accepts_both()
    {
        var validator = new SellerCapacityValidator(NullLogger<SellerCapacityValidator>.Instance);
        var cache = CacheWithLong(quantity: 10);
        var sell = MarketSell(orderId: 201, qty: 30, reserved: 10);
        var ordersById = new Dictionary<int, Order> { [201] = sell };
        var empty = new Dictionary<(int, int), Position>();

        var (err, accepted, rejected) = validator.Filter(
            new[] { Fill(201, 300, 10), Fill(201, 301, 20) }, ordersById, cache, empty, default);

        Assert.Null(err);
        Assert.Equal(2, accepted.Count);
        Assert.Empty(rejected);
    }

    [Fact]
    public void Limit_sell_beyond_holdings_still_rejects()
    {
        var validator = new SellerCapacityValidator(NullLogger<SellerCapacityValidator>.Instance);
        var cache = CacheWithLong(quantity: 10);
        var limit = new Order
        {
            UserId = Seller, StockId = StockId, Quantity = 30, Price = 50m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        limit.OrderId = 202;
        limit.TakeSellReservation(10);
        var ordersById = new Dictionary<int, Order> { [202] = limit };
        var empty = new Dictionary<(int, int), Position>();

        var (err, accepted, rejected) = validator.Filter(
            new[] { Fill(202, 300, 30) }, ordersById, cache, empty, default);

        Assert.Null(err);
        Assert.Empty(accepted);
        Assert.Single(rejected);
    }

    // Model-level invariant that justifies "consume the long FIRST" in TradeSettler's flip branch:
    // a position holding a share reservation cannot be pushed short, but consuming the reservation
    // to zero first makes the cross legal.
    [Fact]
    public void Crossing_short_while_holding_reservation_throws_until_reservation_consumed()
    {
        // Pushing a position negative while a share reservation is still held is illegal — the guard
        // fires only once Quantity actually goes < 0 (ApplyDelta mutates then throws, so use a throw-
        // away position for the negative case). This is exactly why TradeSettler's flip consumes the
        // long reservation FIRST, then opens the short.
        var locked = new Position { UserId = Seller, StockId = StockId, Quantity = 10 };
        locked.ReserveStock(10);
        Assert.Throws<InvalidOperationException>(() => locked.ApplyDelta(-20)); // 10 → -10 with reservation

        // Correct order: consume the long first (Quantity + ReservedQuantity → 0), then the cross is legal.
        var p = new Position { UserId = Seller, StockId = StockId, Quantity = 10 };
        p.ReserveStock(10);
        p.ConsumeReservedStock(10);
        Assert.Equal(0, p.Quantity);
        Assert.Equal(0, p.ReservedQuantity);

        p.ApplyDelta(-20);
        p.TakeShortCollateral(1000m, CurrencyType.USD);
        Assert.Equal(-20, p.Quantity);
        Assert.True(p.IsValid());
    }
}
