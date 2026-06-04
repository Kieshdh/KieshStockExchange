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
/// §3.6 P4 Step 0 regression: the reservation reconciler/clamp must count an armed (Pending)
/// stop's reservation. An armed sell-stop reserves shares on Position.ReservedQuantity at arm
/// time but is not IsOpen — before the fix the reconciler summed only open limit sells, so the
/// stop's reservation read as a phantom and a clamp=true pass zeroed it, silently unprotecting
/// the position (and, in P4, the bracket SL's pooled reservation). These tests pin the fix and
/// guard the latent pre-P4 bug for standalone armed stops.
/// </summary>
public class BracketReconcileTests
{
    private const int UserId = 1;
    private const int StockId = 10;

    private static AccountsCache NewCache(IOrderRegistry registry)
    {
        var db = new Mock<IDataBaseService>(MockBehavior.Loose).Object;
        var ledger = new Mock<IReservationLedger>(MockBehavior.Loose).Object;
        return new AccountsCache(db, registry, ledger, NullLogger<AccountsCache>.Instance);
    }

    private static Order ArmedSellStop(int orderId, int qty)
    {
        var o = new Order
        {
            UserId = UserId,
            StockId = StockId,
            Quantity = qty,
            Price = 0m,                 // stop-market sell has Price 0
            StopPrice = 50m,
            CurrencyType = CurrencyType.USD,
            Side = OrderSide.Sell,
            Entry = EntryType.Market,
            Stop = StopKind.Stop,
        };
        o.OrderId = orderId;
        o.Arm();                         // Status = Pending (armed, off-book)
        o.TakeSellReservation(qty);      // pooled share reservation it holds
        return o;
    }

    [Fact]
    public async Task ArmedSellStop_SurvivesClampReconcile()
    {
        var registry = new OrderRegistry();
        var cache = NewCache(registry);

        // Position holds 10 shares, all reserved by the armed sell-stop (the bracket SL pool).
        var pos = new Position { UserId = UserId, StockId = StockId, Quantity = 10 };
        pos.ReserveStock(10);
        cache.TrackNewPosition(pos);

        registry.Register(ArmedSellStop(orderId: 100, qty: 10));

        var mismatches = await cache.ReconcileReservationsAsync(clamp: true);

        // The armed stop is now counted, so expected == actual == 10: no mismatch, no clamp.
        Assert.Empty(mismatches);
        Assert.Equal(10, pos.ReservedQuantity);
    }

    [Fact]
    public void Registry_ReturnsArmedSellStops_AndExcludesOpenAndClosed()
    {
        var registry = new OrderRegistry();

        var armed = ArmedSellStop(orderId: 100, qty: 10);
        registry.Register(armed);

        // An open limit sell (not a stop) must NOT be returned by the armed-stop helper.
        var openLimit = new Order
        {
            UserId = UserId, StockId = StockId, Quantity = 5, Price = 60m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Limit,
        };
        openLimit.OrderId = 101;
        openLimit.TakeSellReservation(5);
        registry.Register(openLimit);

        var armedStops = registry.GetArmedSellStopsForUser(UserId, StockId);

        Assert.Single(armedStops);
        Assert.Equal(100, armedStops[0].OrderId);
    }

    [Fact]
    public async Task ArmedSellStop_StillCounted_WhenResedingOpenLimitAlsoPresent()
    {
        var registry = new OrderRegistry();
        var cache = NewCache(registry);

        // 8 shares reserved: 5 by an open limit sell + 3 by an armed sell-stop.
        var pos = new Position { UserId = UserId, StockId = StockId, Quantity = 8 };
        pos.ReserveStock(8);
        cache.TrackNewPosition(pos);

        var openLimit = new Order
        {
            UserId = UserId, StockId = StockId, Quantity = 5, Price = 60m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Limit,
        };
        openLimit.OrderId = 101;
        openLimit.TakeSellReservation(5);
        registry.Register(openLimit);
        registry.Register(ArmedSellStop(orderId: 100, qty: 3));

        var mismatches = await cache.ReconcileReservationsAsync(clamp: true);

        Assert.Empty(mismatches);
        Assert.Equal(8, pos.ReservedQuantity);
    }
}
