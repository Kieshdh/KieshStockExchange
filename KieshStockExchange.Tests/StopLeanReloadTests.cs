using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §B3 lean reload (Bots:LeanReload). When on, RefreshAssetsAsync fetches only open LIMITS + a per-bot armed-stop
/// COUNT (not the ~1.18M armed-stop Orders), so the reload is O(limits); the cap adds ArmedStopCount and
/// replace-old sources victims from a targeted query. Off ⇒ full hydration (byte-identical, covered by
/// StopLeakTests). Definitive gate is the CK-clean 45-min soak; these lock the mechanics.
/// </summary>
public class StopLeanReloadTests
{
    private const CurrencyType USD = CurrencyType.USD;

    private static (AiBotStateService svc, Mock<IBotMaintenanceQueries> maint,
                    Mock<IDataBaseService> db, Mock<IOrderExecutionService> orders) Build(bool leanReload)
    {
        var db = new Mock<IDataBaseService>();
        var accounts = new Mock<IAccountsCache>();
        var orders = new Mock<IOrderExecutionService>();
        var maint = new Mock<IBotMaintenanceQueries>();
        var stats = new BotStatsLogger(NullLogger<BotStatsLogger>.Instance);

        accounts.Setup(a => a.EnsureLoadedAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Position>());

        var svc = new AiBotStateService(db.Object, accounts.Object, orders.Object, stats,
            NullLogger<AiBotStateService>.Instance, maint.Object,
            pruneLimitOnly: leanReload, leanReload: leanReload);
        return (svc, maint, db, orders);
    }

    private static AiBotContext CtxWith(int userId)
    {
        var ctx = new AiBotContext(new Mock<IAccountsCache>().Object);
        var u = new AIUser
        {
            AiUserId = userId, UserId = userId, Seed = 1, StrategyCode = (int)AiStrategy.Random,
            IsEnabled = true, DecisionIntervalSeconds = 1,
        };
        ctx.AiUsersByAiUserId[userId] = u; ctx.AiUsersByUserId[userId] = u;
        return ctx;
    }

    private static Order Limit(int id, int userId, int stockId, OrderSide side) => new()
    {
        OrderId = id, UserId = userId, StockId = stockId, CurrencyType = USD, Side = side,
        Entry = EntryType.Limit, Stop = StopKind.None, Status = Order.Statuses.Open, Quantity = 5, Price = 100m,
    };

    [Fact]
    public async Task LeanReload_populates_limits_only_and_the_armed_stop_count()
    {
        var (svc, maint, db, _) = Build(leanReload: true);
        var ctx = CtxWith(10);

        maint.Setup(m => m.GetOpenLimitOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<Order> { Limit(1, 10, 100, OrderSide.Buy) });
        maint.Setup(m => m.GetArmedStopCountsByUserAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Dictionary<int, int> { [10] = 7 });

        await svc.RefreshAssetsAsync(ctx, CancellationToken.None);

        Assert.True(ctx.OpenOrders[10].ContainsKey(1));      // the limit
        Assert.Single(ctx.OpenOrders[10]);                   // ONLY the limit — no armed stops hydrated
        Assert.Equal(7, ctx.ArmedStopCount[10]);             // count came from the GROUP-BY
        // The shared (O(pool)) fetch is NOT used when lean.
        db.Verify(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
        maint.Verify(m => m.GetOpenLimitOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeanReload_off_uses_the_full_fetch_and_no_count()
    {
        var (svc, maint, db, _) = Build(leanReload: false);
        var ctx = CtxWith(10);
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order> { Limit(1, 10, 100, OrderSide.Buy) });

        await svc.RefreshAssetsAsync(ctx, CancellationToken.None);

        db.Verify(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Once);
        maint.Verify(m => m.GetOpenLimitOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(ctx.ArmedStopCount);                    // count path not taken when off
    }

    [Fact]
    public async Task ReplaceOld_lean_sources_victims_from_query_and_decrements_count()
    {
        var (svc, maint, _, orders) = Build(leanReload: true);
        var ctx = CtxWith(10);
        ctx.ArmedStopCount[10] = 2;                          // as if the last reload counted 2

        maint.Setup(m => m.GetStandaloneArmedStopIdsAsync(10, 100, OrderSide.Sell, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<int> { 77 });
        orders.Setup(o => o.CancelOrderAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((int id, CancellationToken _) => OrderResultFactory.Cancelled(new Order { OrderId = id }));

        await svc.CancelPriorStandaloneStopsAsync(ctx, 10, 100, OrderSide.Sell, CancellationToken.None);

        orders.Verify(o => o.CancelOrderAsync(77, It.IsAny<CancellationToken>()), Times.Once);
        orders.VerifyNoOtherCalls();                         // safe per-order path only, no batch
        Assert.Equal(1, ctx.ArmedStopCount[10]);             // decremented on success
    }

    [Fact]
    public async Task ReplaceOld_lean_clamps_count_at_zero()
    {
        var (svc, maint, _, orders) = Build(leanReload: true);
        var ctx = CtxWith(10);
        ctx.ArmedStopCount[10] = 0;                          // stale: an arm since last reload isn't counted yet

        maint.Setup(m => m.GetStandaloneArmedStopIdsAsync(10, 100, OrderSide.Sell, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<int> { 77 });
        orders.Setup(o => o.CancelOrderAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((int id, CancellationToken _) => OrderResultFactory.Cancelled(new Order { OrderId = id }));

        await svc.CancelPriorStandaloneStopsAsync(ctx, 10, 100, OrderSide.Sell, CancellationToken.None);

        Assert.Equal(0, ctx.ArmedStopCount[10]);             // Max(0, 0-1) — never negative
    }
}
