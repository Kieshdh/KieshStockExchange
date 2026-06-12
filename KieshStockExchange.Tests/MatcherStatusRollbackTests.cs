using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace KieshStockExchange.Tests;

/// <summary>
/// R4 §0001 acceptance: the matcher (MatchingEngine.Match + OrderBook.ApplyMakerFill) must
/// capture the pre-mutation <see cref="Order.Status"/> of every order it touches into
/// <see cref="TradeBatchScope.OrderStatusSnapshots"/> before <see cref="Order.Fill(int)"/>
/// flips it to <c>Filled</c>. A subsequent <c>TradeSettler.RestoreSnapshots</c> call must
/// replay the snapshot to restore the in-memory Status to its pre-batch value.
///
/// This test exercises the matcher directly (no full settle stack) — the contract under test
/// is "matcher populates the dict" + "snapshot value is the pre-match value." The
/// end-to-end batch+rollback path is covered by the existing group-dispatch flow plus the
/// FlipBatchInterleavingTests in §0002.
/// </summary>
public class MatcherStatusRollbackTests
{
    private const int StockId = 42;
    private const CurrencyType Ccy = CurrencyType.USD;

    private static Order BuyMaker(int orderId, int qty, decimal price) => new()
    {
        OrderId = orderId, UserId = 1, StockId = StockId, CurrencyType = Ccy,
        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        Quantity = qty, Price = price,
    };

    private static Order SellTaker(int orderId, int qty, decimal price) => new()
    {
        OrderId = orderId, UserId = 2, StockId = StockId, CurrencyType = Ccy,
        Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
        Quantity = qty, Price = price,
    };

    private static (MatchingEngine matcher, OrderBook book) NewMatcher()
    {
        var matcher = new MatchingEngine(NullLogger<MatchingEngine>.Instance);
        var book = new OrderBook(StockId, Ccy);
        return (matcher, book);
    }

    [Fact]
    public void Match_with_scope_captures_pre_match_status_of_taker_and_maker()
    {
        // Maker rests in the book at Status=Open. Sell taker crosses and fully consumes it.
        var (matcher, book) = NewMatcher();
        var maker = BuyMaker(orderId: 100, qty: 10, price: 50m);
        book.UpsertOrder(maker);

        var taker = SellTaker(orderId: 200, qty: 10, price: 50m);
        var scope = new TradeBatchScope();

        var result = matcher.Match(taker, book, CancellationToken.None, scope);

        // Both touched orders have their pre-match Status captured (Open).
        Assert.True(scope.OrderStatusSnapshots.ContainsKey(taker.OrderId));
        Assert.True(scope.OrderStatusSnapshots.ContainsKey(maker.OrderId));
        Assert.Equal(Order.Statuses.Open, scope.OrderStatusSnapshots[taker.OrderId]);
        Assert.Equal(Order.Statuses.Open, scope.OrderStatusSnapshots[maker.OrderId]);

        // Sanity: the matcher actually mutated Status to Filled (so the snapshot is
        // the pre-mutation value, not the current value).
        Assert.Equal(Order.Statuses.Filled, taker.Status);
        Assert.Equal(Order.Statuses.Filled, maker.Status);
        Assert.Single(result.Fills);
    }

    [Fact]
    public void Match_without_scope_does_not_throw_and_skips_capture()
    {
        // Default-null scope: single-taker call sites at OrderExecutionService.cs:155 and
        // :469 rely on this path. RollbackMatch's Status=Open hardcode covers them.
        var (matcher, book) = NewMatcher();
        book.UpsertOrder(BuyMaker(orderId: 100, qty: 5, price: 75m));
        var taker = SellTaker(orderId: 200, qty: 5, price: 75m);

        var result = matcher.Match(taker, book, CancellationToken.None);

        Assert.Single(result.Fills);
        Assert.Equal(Order.Statuses.Filled, taker.Status);
    }

    [Fact]
    public void Match_with_scope_idempotent_on_multi_fill_taker()
    {
        // Taker walks two maker levels; matcher loops twice. TryAdd must keep the original
        // taker snapshot (set on first iteration) rather than overwrite with the post-first-fill
        // value. Open is the pre-match value for both makers, so this also confirms makers are
        // captured separately.
        var (matcher, book) = NewMatcher();
        book.UpsertOrder(BuyMaker(orderId: 100, qty: 6, price: 50m));
        book.UpsertOrder(BuyMaker(orderId: 101, qty: 4, price: 49m));

        var taker = SellTaker(orderId: 200, qty: 10, price: 49m);
        var scope = new TradeBatchScope();

        var result = matcher.Match(taker, book, CancellationToken.None, scope);

        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(Order.Statuses.Open, scope.OrderStatusSnapshots[200]);
        Assert.Equal(Order.Statuses.Open, scope.OrderStatusSnapshots[100]);
        Assert.Equal(Order.Statuses.Open, scope.OrderStatusSnapshots[101]);
    }

    [Fact]
    public void Replaying_snapshot_restores_status_to_pre_match()
    {
        // The actual rollback path is in TradeSettler.RestoreSnapshots, but the per-order
        // Status restore loop is just `if (o.Status != prev) o.Status = prev;` against the
        // dict. This test reproduces that body directly to confirm the contract end-to-end
        // without standing up the settler+accounts cache.
        var (matcher, book) = NewMatcher();
        var maker = BuyMaker(orderId: 100, qty: 10, price: 50m);
        book.UpsertOrder(maker);
        var taker = SellTaker(orderId: 200, qty: 10, price: 50m);
        var scope = new TradeBatchScope();

        matcher.Match(taker, book, CancellationToken.None, scope);
        Assert.Equal(Order.Statuses.Filled, taker.Status);
        Assert.Equal(Order.Statuses.Filled, maker.Status);

        // Replay snapshot — mirrors the loop at TradeSettler.cs:854-858.
        foreach (var (orderId, prev) in scope.OrderStatusSnapshots)
        {
            var target = orderId == taker.OrderId ? taker : maker;
            if (target.Status != prev) target.Status = prev;
        }

        Assert.Equal(Order.Statuses.Open, taker.Status);
        Assert.Equal(Order.Statuses.Open, maker.Status);
    }
}
