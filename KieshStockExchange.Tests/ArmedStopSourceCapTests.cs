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
/// §source-cap (docs/ultraplan-prompt-maint-tick-scaling-md-pure-abelson.md, Phase 1) — the per-bot armed-stop
/// CAP that bounds the pool at placement. This suite locks the count MECHANICS (the definitive gate is the
/// CK-clean prod soak):
///   • <see cref="AiBotStateService.ShouldCountArm"/> — the pure predicate deciding when a placement counts
///     (only a resting STANDALONE armed Pending stop of a protective kind, and only when cap>0 && LeanReload).
///     This is the only unit-reachable coverage of the two INLINE arm-result sites in AiTradeService (a
///     BackgroundService that is not constructible in a test), which delegate their +1 to this predicate.
///   • <see cref="AiBotStateService.NoteArmedStopPlaced"/> — the single-owner +1 mirror of replace-old's −1.
///   • The ~60s reload GROUP-BY CLEARS + repopulates ctx.ArmedStopCount, so intra-window deltas can't drift.
/// Off / lean-off ⇒ every path is a no-op ⇒ byte-identical.
/// </summary>
public class ArmedStopSourceCapTests
{
    private const CurrencyType USD = CurrencyType.USD;

    // ---- factories ------------------------------------------------------------------------------------

    private static Order ArmedStop(int id, OrderSide side = OrderSide.Sell, int? parent = null) => new()
    {
        OrderId = id, UserId = 10, StockId = 100, CurrencyType = USD, Side = side,
        Entry = EntryType.Market, Stop = StopKind.Stop, Status = Order.Statuses.Pending,
        ParentOrderId = parent, Quantity = 5, Price = 100m,
    };

    // A stop that FILLED at placement (no resting Pending row remains) — the buy stop-limit over-count hole.
    private static Order FilledStop(int id) => new()
    {
        OrderId = id, UserId = 10, StockId = 100, CurrencyType = USD, Side = OrderSide.Buy,
        Entry = EntryType.Limit, Stop = StopKind.Stop, Status = Order.Statuses.Filled, Quantity = 5, Price = 100m,
    };

    private static OrderResult Placed(Order? o, OrderStatus status = OrderStatus.Success) =>
        new() { Status = status, PlacedOrder = o, FillTransactions = new List<Transaction>() };

    private static (AiBotStateService svc, AiBotContext ctx, Mock<IBotMaintenanceQueries> maint,
                    Mock<IDataBaseService> db, Mock<IAccountsCache> accounts)
        BuildState(int cap, bool leanReload)
    {
        var db = new Mock<IDataBaseService>();
        var accounts = new Mock<IAccountsCache>();
        var orders = new Mock<IOrderExecutionService>();
        var maint = new Mock<IBotMaintenanceQueries>();
        var stats = new BotStatsLogger(NullLogger<BotStatsLogger>.Instance);
        var svc = new AiBotStateService(db.Object, accounts.Object, orders.Object, stats,
            NullLogger<AiBotStateService>.Instance, maint.Object,
            pruneLimitOnly: leanReload, leanReload: leanReload, maxArmedStopsPerBot: cap);
        var ctx = new AiBotContext(new Mock<IAccountsCache>().Object);
        return (svc, ctx, maint, db, accounts);
    }

    private static AIUser Bot(int id = 10) => new()
    {
        AiUserId = id, UserId = id, Seed = 1, StrategyCode = (int)AiStrategy.Random,
        IsEnabled = true, DecisionIntervalSeconds = 1,
    };

    // ---- ShouldCountArm (pure) ------------------------------------------------------------------------

    [Theory]
    [InlineData(BotAdvancedKind.StopMarketSell)]
    [InlineData(BotAdvancedKind.StopMarketBuy)]
    [InlineData(BotAdvancedKind.TrailingStopSell)]
    public void ShouldCountArm_true_for_resting_standalone_protective_stop(BotAdvancedKind kind)
    {
        Assert.True(AiBotStateService.ShouldCountArm(kind, Placed(ArmedStop(1)), maxArmedStopsPerBot: 3, leanReload: true));
    }

    [Theory]
    [InlineData(BotAdvancedKind.LongBracket)]
    [InlineData(BotAdvancedKind.ShortBracket)]
    [InlineData(BotAdvancedKind.ShortOpen)]
    public void ShouldCountArm_false_for_non_protective_kinds(BotAdvancedKind kind)
    {
        Assert.False(AiBotStateService.ShouldCountArm(kind, Placed(ArmedStop(1)), maxArmedStopsPerBot: 3, leanReload: true));
    }

    [Fact]
    public void ShouldCountArm_false_when_off_or_lean_off()
    {
        var r = Placed(ArmedStop(1));
        Assert.False(AiBotStateService.ShouldCountArm(BotAdvancedKind.StopMarketSell, r, maxArmedStopsPerBot: 0, leanReload: true));
        Assert.False(AiBotStateService.ShouldCountArm(BotAdvancedKind.StopMarketSell, r, maxArmedStopsPerBot: 3, leanReload: false));
    }

    [Fact]
    public void ShouldCountArm_false_when_no_resting_pending_row()
    {
        // No placed order (failure), a bracket child (has a parent), and a stop that filled at placement all
        // leave no standalone Pending row to count — PlacedSuccessfully alone would over-count the last one.
        Assert.False(AiBotStateService.ShouldCountArm(BotAdvancedKind.StopMarketSell, Placed(null, OrderStatus.OperationFailed), 3, true));
        Assert.False(AiBotStateService.ShouldCountArm(BotAdvancedKind.StopMarketSell, Placed(ArmedStop(1, parent: 7)), 3, true));
        Assert.False(AiBotStateService.ShouldCountArm(BotAdvancedKind.StopMarketBuy, Placed(FilledStop(1), OrderStatus.Filled), 3, true));
    }

    // ---- NoteArmedStopPlaced --------------------------------------------------------------------------

    [Fact]
    public void NoteArmedStopPlaced_increments_on_a_counted_arm()
    {
        var (svc, ctx, _, _, _) = BuildState(cap: 3, leanReload: true);
        ctx.ArmedStopCount[10] = 1;

        svc.NoteArmedStopPlaced(ctx, Bot(), BotAdvancedKind.StopMarketSell, Placed(ArmedStop(1)));

        Assert.Equal(2, ctx.ArmedStopCount[10]);
    }

    [Fact]
    public void NoteArmedStopPlaced_seeds_from_zero_when_absent()
    {
        var (svc, ctx, _, _, _) = BuildState(cap: 3, leanReload: true);

        svc.NoteArmedStopPlaced(ctx, Bot(), BotAdvancedKind.StopMarketBuy, Placed(ArmedStop(1, OrderSide.Buy)));

        Assert.Equal(1, ctx.ArmedStopCount.GetValueOrDefault(10));
    }

    [Fact]
    public void NoteArmedStopPlaced_noop_when_cap_off()
    {
        var (svc, ctx, _, _, _) = BuildState(cap: 0, leanReload: true);

        svc.NoteArmedStopPlaced(ctx, Bot(), BotAdvancedKind.StopMarketSell, Placed(ArmedStop(1)));

        Assert.False(ctx.ArmedStopCount.ContainsKey(10));   // byte-identical guard
    }

    [Fact]
    public void NoteArmedStopPlaced_noop_for_bracket_and_failed()
    {
        var (svc, ctx, _, _, _) = BuildState(cap: 3, leanReload: true);

        svc.NoteArmedStopPlaced(ctx, Bot(), BotAdvancedKind.LongBracket, Placed(ArmedStop(1)));
        svc.NoteArmedStopPlaced(ctx, Bot(), BotAdvancedKind.StopMarketSell, Placed(null, OrderStatus.OperationFailed));

        Assert.False(ctx.ArmedStopCount.ContainsKey(10));
    }

    // ---- reload re-baseline ---------------------------------------------------------------------------

    [Fact]
    public async Task Reload_overwrites_the_intra_window_count_no_drift()
    {
        var (svc, ctx, maint, db, accounts) = BuildState(cap: 3, leanReload: true);
        var u = Bot();
        ctx.AiUsersByAiUserId[10] = u; ctx.AiUsersByUserId[10] = u;
        ctx.ArmedStopCount[10] = 99;   // as if arms(+)/replaces(-) had accumulated this window

        accounts.Setup(a => a.EnsureLoadedAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Position>());
        maint.Setup(m => m.GetOpenLimitOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<Order>());
        maint.Setup(m => m.GetArmedStopCountsByUserAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Dictionary<int, int> { [10] = 7 });

        await svc.RefreshAssetsAsync(ctx, CancellationToken.None);

        Assert.Equal(7, ctx.ArmedStopCount[10]);   // GROUP-BY re-baseline replaces, never adds (99 gone)
    }
}
