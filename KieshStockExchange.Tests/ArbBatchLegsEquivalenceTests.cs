using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.HostedServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// STRETCH (Bots:Arbitrage:BatchLegs, default-off / unbaked) tests for the two new batched true-market
/// entry points the arb cohort's two-pass round-trip is built on:
/// <see cref="OrderEntryService.PlaceTrueMarketBuyBatchAsync"/> (leg1) and
/// <see cref="OrderEntryService.PlaceTrueMarketSellBatchAsync"/> (leg2). Each must build orders with
/// the SAME shape as its per-order sibling (PlaceTrueMarket{Buy,Sell}OrderAsync), route the valid ones
/// to the engine's plain batch path in submission order, map results back by index, and isolate a
/// pre-validation reject without sending it to the engine.
///
/// The full two-pass orchestration (leg2 sized from each leg1 fill) lives in ArbitrageDecisionService
/// and is exercised by the unbaked soak, not bake-gated this round.
/// </summary>
public class ArbBatchLegsEquivalenceTests
{
    private const int StockId = 10;

    private static (OrderEntryService Entry, List<List<Order>> Captured) NewEntry()
    {
        var captured = new List<List<Order>>();

        var engine = new Mock<IOrderExecutionService>();
        engine.Setup(e => e.PlaceAndMatchBatchAsync(It.IsAny<IReadOnlyList<Order>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((IReadOnlyList<Order> orders, CancellationToken _) =>
              {
                  captured.Add(orders.ToList());
                  return (IReadOnlyList<OrderResult>)orders
                      .Select(o => OrderResultFactory.Success(o, new List<Transaction>())).ToList();
              });

        var stocks = new Mock<IStockService>();
        Stock? stockOut = new Stock();
        stocks.Setup(s => s.TryGetById(It.IsAny<int>(), out stockOut)).Returns(true);
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(true);
        var validator = new OrderValidator(stocks.Object);

        var entry = new OrderEntryService(
            engine.Object,
            NullLogger<OrderEntryService>.Instance,
            validator,
            new Mock<IMarketDataService>().Object,
            new Mock<IDataBaseService>().Object,
            new Mock<IStopWatcher>().Object,
            new Mock<IOrderCacheService>().Object,
            new OrderRegistry());

        return (entry, captured);
    }

    [Fact]
    public async Task BuyBatch_routes_valid_budget_buys_in_order_and_isolates_rejects()
    {
        var (entry, captured) = NewEntry();

        var requests = new[]
        {
            new TrueMarketBuyBatchRequest(UserId: 1, StockId, Quantity: 5, BuyBudget: 1_000m, Currency: CurrencyType.USD),
            new TrueMarketBuyBatchRequest(UserId: 2, StockId, Quantity: 0, BuyBudget: 1_000m, Currency: CurrencyType.USD), // reject: qty<=0
            new TrueMarketBuyBatchRequest(UserId: 3, StockId, Quantity: 7, BuyBudget: 2_000m, Currency: CurrencyType.USD),
        };

        var results = await entry.PlaceTrueMarketBuyBatchAsync(requests);

        Assert.True(results[0].PlacedSuccessfully);
        Assert.False(results[1].PlacedSuccessfully);
        Assert.True(results[2].PlacedSuccessfully);

        // Only the two valid requests reached the engine, once, in submission order.
        Assert.Single(captured);
        Assert.Equal(new[] { 1, 3 }, captured[0].Select(o => o.UserId).ToArray());
        Assert.Equal(new[] { 5, 7 }, captured[0].Select(o => o.Quantity).ToArray());
        // Built as budget-capped true-market buys (Price 0, positive BuyBudget) — same shape as the
        // per-order PlaceTrueMarketBuyOrderAsync.
        Assert.All(captured[0], o =>
        {
            Assert.Equal(OrderSide.Buy, o.Side);
            Assert.Equal(EntryType.Market, o.Entry);
            Assert.Equal(0m, o.Price);
            Assert.True(o.BuyBudget > 0m);
        });
    }

    [Fact]
    public async Task SellBatch_routes_valid_sells_in_order_and_isolates_rejects()
    {
        var (entry, captured) = NewEntry();

        var requests = new[]
        {
            new TrueMarketSellBatchRequest(UserId: 1, StockId, Quantity: 5, Currency: CurrencyType.USD),
            new TrueMarketSellBatchRequest(UserId: 2, StockId, Quantity: 0, Currency: CurrencyType.USD), // reject: qty<=0
            new TrueMarketSellBatchRequest(UserId: 3, StockId, Quantity: 3, Currency: CurrencyType.USD),
        };

        var results = await entry.PlaceTrueMarketSellBatchAsync(requests);

        Assert.True(results[0].PlacedSuccessfully);
        Assert.False(results[1].PlacedSuccessfully);
        Assert.True(results[2].PlacedSuccessfully);

        Assert.Single(captured);
        Assert.Equal(new[] { 1, 3 }, captured[0].Select(o => o.UserId).ToArray());
        Assert.Equal(new[] { 5, 3 }, captured[0].Select(o => o.Quantity).ToArray());
        // Built as true-market sells (Price 0, no BuyBudget) — same shape as PlaceTrueMarketSellOrderAsync.
        Assert.All(captured[0], o =>
        {
            Assert.Equal(OrderSide.Sell, o.Side);
            Assert.Equal(EntryType.Market, o.Entry);
            Assert.Equal(0m, o.Price);
            Assert.Null(o.BuyBudget);
        });
    }

    [Fact]
    public async Task Empty_request_list_is_a_no_op()
    {
        var (entry, captured) = NewEntry();
        Assert.Empty(await entry.PlaceTrueMarketBuyBatchAsync(Array.Empty<TrueMarketBuyBatchRequest>()));
        Assert.Empty(await entry.PlaceTrueMarketSellBatchAsync(Array.Empty<TrueMarketSellBatchRequest>()));
        Assert.Empty(captured); // engine never touched
    }
}
