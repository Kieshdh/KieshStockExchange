using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// Workstream 1 (docs/ultraplan-prompt-maint-tick-scaling.md) — the armed-stop maint fix.
/// (a) replace-old: <see cref="AiBotStateService.CancelPriorStandaloneStopsAsync"/> cancels only a bot's
///     matching (stock, side) STANDALONE armed stops via the SAFE per-order path (never the batch path),
///     excluding bracket children — so a bot moves its stop instead of stacking one per draw.
/// (b) B2: when <c>Bots:PruneLimitOnly</c> is on, <c>ctx.OpenLimitOrders</c> mirrors only the resting limits
///     of <c>ctx.OpenOrders</c> at cold-load + placement, so the prune scans O(limits) not O(limits+stops).
/// Both default-off ⇒ the index is never populated and no stops are pre-cancelled (byte-identical).
/// The definitive gate is the CK-clean multi-hour soak; these lock the mechanics.
/// </summary>
public class StopLeakTests
{
    private const CurrencyType USD = CurrencyType.USD;

    private static (AiBotStateService svc, Mock<IOrderExecutionService> orders,
                    Mock<IDataBaseService> db, Mock<IAccountsCache> accounts) Build(bool pruneLimitOnly)
    {
        var db = new Mock<IDataBaseService>();
        var accounts = new Mock<IAccountsCache>();
        var orders = new Mock<IOrderExecutionService>();
        var stats = new BotStatsLogger(NullLogger<BotStatsLogger>.Instance);
        var svc = new AiBotStateService(db.Object, accounts.Object, orders.Object, stats,
            NullLogger<AiBotStateService>.Instance, pruneLimitOnly: pruneLimitOnly);
        return (svc, orders, db, accounts);
    }

    private static Order Limit(int id, int userId, int stockId, OrderSide side) => new()
    {
        OrderId = id, UserId = userId, StockId = stockId, CurrencyType = USD, Side = side,
        Entry = EntryType.Limit, Stop = StopKind.None, Status = Order.Statuses.Open, Quantity = 5, Price = 100m,
    };

    private static Order ArmedStop(int id, int userId, int stockId, OrderSide side, int? parent = null) => new()
    {
        OrderId = id, UserId = userId, StockId = stockId, CurrencyType = USD, Side = side,
        Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        ParentOrderId = parent, Quantity = 5, Price = 100m,
    };

    private static OrderResult Placed(Order o) =>
        new() { Status = OrderStatus.Success, PlacedOrder = o, FillTransactions = new List<Transaction>() };

    // ---- (a) replace-old ---------------------------------------------------------------------------

    [Fact]
    public async Task ReplaceOld_cancels_only_matching_standalone_armed_stops_via_safe_path()
    {
        var (svc, orders, _, _) = Build(pruneLimitOnly: false);
        orders.Setup(o => o.CancelOrderAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((int id, CancellationToken _) => OrderResultFactory.Cancelled(new Order { OrderId = id }));

        var ctx = new AiBotContext(new Mock<IAccountsCache>().Object);
        const int u = 10, stock = 100;
        ctx.OpenOrders[u] = new Dictionary<int, Order>
        {
            [1] = ArmedStop(1, u, stock, OrderSide.Sell),             // MATCH → cancel
            [2] = ArmedStop(2, u, stock, OrderSide.Buy),              // wrong side → keep
            [3] = ArmedStop(3, u, 999,  OrderSide.Sell),             // wrong stock → keep
            [4] = ArmedStop(4, u, stock, OrderSide.Sell, parent: 7),  // bracket child → keep
            [5] = Limit(5, u, stock, OrderSide.Sell),                 // resting limit → keep
        };

        await svc.CancelPriorStandaloneStopsAsync(ctx, u, stock, OrderSide.Sell, CancellationToken.None);

        // ONLY #1 cancelled, and ONLY via the SAFE per-order path (no batch, no other calls).
        orders.Verify(o => o.CancelOrderAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        orders.VerifyNoOtherCalls();

        Assert.False(ctx.OpenOrders[u].ContainsKey(1));  // removed on success
        Assert.True(ctx.OpenOrders[u].ContainsKey(2));
        Assert.True(ctx.OpenOrders[u].ContainsKey(3));
        Assert.True(ctx.OpenOrders[u].ContainsKey(4));   // bracket child untouched
        Assert.True(ctx.OpenOrders[u].ContainsKey(5));   // resting limit untouched
    }

    [Fact]
    public async Task ReplaceOld_noop_when_no_matching_prior_stop()
    {
        var (svc, orders, _, _) = Build(pruneLimitOnly: false);
        var ctx = new AiBotContext(new Mock<IAccountsCache>().Object);
        ctx.OpenOrders[10] = new Dictionary<int, Order> { [1] = Limit(1, 10, 100, OrderSide.Sell) };

        await svc.CancelPriorStandaloneStopsAsync(ctx, 10, 100, OrderSide.Sell, CancellationToken.None);

        orders.VerifyNoOtherCalls();                     // nothing to cancel
        Assert.True(ctx.OpenOrders[10].ContainsKey(1));
    }

    // ---- (b) B2 limit-only index --------------------------------------------------------------------

    [Fact]
    public void Index_tracks_placed_limits_and_ignores_stops_when_on()
    {
        var (svc, _, _, _) = Build(pruneLimitOnly: true);
        var ctx = new AiBotContext(new Mock<IAccountsCache>().Object);

        svc.ApplyResultToCache(ctx, Placed(Limit(1, 10, 100, OrderSide.Buy)));
        Assert.True(ctx.OpenOrders[10].ContainsKey(1));
        Assert.True(ctx.OpenLimitOrders[10].ContainsKey(1));   // mirrored into the index

        // An armed stop is not IsOpenLimitOrder ⇒ ApplyResultToCache adds it to NEITHER map.
        svc.ApplyResultToCache(ctx, Placed(ArmedStop(2, 10, 100, OrderSide.Sell)));
        Assert.False(ctx.OpenOrders[10].ContainsKey(2));
        Assert.False(ctx.OpenLimitOrders[10].ContainsKey(2));
    }

    [Fact]
    public void Index_not_populated_when_off()
    {
        var (svc, _, _, _) = Build(pruneLimitOnly: false);
        var ctx = new AiBotContext(new Mock<IAccountsCache>().Object);

        svc.ApplyResultToCache(ctx, Placed(Limit(1, 10, 100, OrderSide.Buy)));
        Assert.True(ctx.OpenOrders[10].ContainsKey(1));         // OpenOrders unchanged (byte-identical)
        Assert.False(ctx.OpenLimitOrders.ContainsKey(10));      // index never touched
    }

    [Fact]
    public async Task ColdLoad_builds_limit_only_index_when_on()
    {
        var (svc, _, db, accounts) = Build(pruneLimitOnly: true);
        var ctx = new AiBotContext(accounts.Object);
        var u = new AIUser
        {
            AiUserId = 10, UserId = 10, Seed = 1, StrategyCode = (int)AiStrategy.Random,
            IsEnabled = true, DecisionIntervalSeconds = 1,
        };
        ctx.AiUsersByAiUserId[10] = u; ctx.AiUsersByUserId[10] = u;

        accounts.Setup(a => a.EnsureLoadedAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Position>());
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order> { Limit(1, 10, 100, OrderSide.Buy), ArmedStop(2, 10, 100, OrderSide.Sell) });

        await svc.RefreshAssetsAsync(ctx, CancellationToken.None);

        Assert.True(ctx.OpenOrders[10].ContainsKey(1));         // full set holds both
        Assert.True(ctx.OpenOrders[10].ContainsKey(2));
        Assert.True(ctx.OpenLimitOrders[10].ContainsKey(1));    // index: the limit
        Assert.False(ctx.OpenLimitOrders[10].ContainsKey(2));   // index excludes the armed stop
    }
}
