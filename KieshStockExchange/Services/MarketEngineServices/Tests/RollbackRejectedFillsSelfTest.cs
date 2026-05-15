using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KieshStockExchange.Services.MarketEngineServices.Tests;

/// <summary>
/// Standalone deterministic harness for
/// <see cref="OrderExecutionService.RollbackRejectedFillsCore"/>. Exercises the three
/// branches that matter for the reservation-leak fix:
///
/// 1. <see cref="SellTakerRejected_RevertsInnocentBuyMaker_NotRemoved"/> — a sell taker
///    partially hits a resting buy maker, validate-pass rejects the fill, the buy maker
///    is reverted (AmountFilled rolled back by exactly the rejected qty, Status flipped
///    back to Open), the level credit is restored, and the maker is NOT cancelled.
///
/// 2. <see cref="SellTakerRejected_RevertsInnocentBuyMaker_FullyRemoved"/> — same flow
///    but the matcher fully filled the buy maker and removed it from the book; the
///    rollback must re-insert the maker with the rejected qty back on the level.
///
/// 3. <see cref="SellMakerRejected_StillCancelsAndReleases"/> — regression check that
///    the seller-cancel + Section-5a release path is unchanged for the case the fix
///    was originally written for (buy taker hits sell maker, fill rejected, seller
///    maker cancelled and its <see cref="Position.ReservedQuantity"/> released).
///
/// Not part of any test runner — invoke from anywhere with:
/// <code>
/// var report = await RollbackRejectedFillsSelfTest.RunAllAsync();
/// foreach (var line in report.Lines) Console.WriteLine(line);
/// </code>
/// </summary>
public static class RollbackRejectedFillsSelfTest
{
    public sealed record SelfTestReport(int Passed, int Failed, IReadOnlyList<string> Lines)
    {
        public bool AllPassed => Failed == 0;
    }

    public static Task<SelfTestReport> RunAllAsync(ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var lines = new List<string>();
        int passed = 0, failed = 0;

        void RunCase(string name, Action<ILogger> body)
        {
            try
            {
                body(loggerFactory!.CreateLogger(name));
                lines.Add($"PASS  {name}");
                passed++;
            }
            catch (Exception ex)
            {
                lines.Add($"FAIL  {name}: {ex.Message}");
                failed++;
            }
        }

        RunCase(nameof(SellTakerRejected_RevertsInnocentBuyMaker_NotRemoved),
                SellTakerRejected_RevertsInnocentBuyMaker_NotRemoved);
        RunCase(nameof(SellTakerRejected_RevertsInnocentBuyMaker_FullyRemoved),
                SellTakerRejected_RevertsInnocentBuyMaker_FullyRemoved);
        RunCase(nameof(SellMakerRejected_StillCancelsAndReleases),
                SellMakerRejected_StillCancelsAndReleases);

        lines.Insert(0, $"--- RollbackRejectedFillsSelfTest: {passed}/{passed + failed} passed ---");
        return Task.FromResult(new SelfTestReport(passed, failed, lines));
    }

    // -----------------------------------------------------------------------
    // Scenarios
    // -----------------------------------------------------------------------

    // The fix's primary case: sell taker partially hits a resting buy maker, fill
    // rejected, buy maker stayed in the book. The rollback must revert the
    // matcher's AmountFilled increment and credit the level qty back, without
    // cancelling the buy maker.
    private static void SellTakerRejected_RevertsInnocentBuyMaker_NotRemoved(ILogger logger)
    {
        var book = new OrderBook(stockId: 100, currency: CurrencyType.USD);

        // Buy maker on the book: LimitBuy 10 @ $5. Place it.
        var buyMaker = BuyLimit(orderId: 1001, userId: 1, stockId: 100, qty: 10, price: 5m);
        book.UpsertOrder(buyMaker);

        // Sell taker: LimitSell 4 @ $5.
        var sellTaker = SellLimit(orderId: 1002, userId: 2, stockId: 100, qty: 4, price: 5m);

        // Simulate the matcher's effect: taker.Fill(4), book.ApplyMakerFill(buyMaker, 4).
        // We don't drive the matcher itself — we replay the state transitions and capture
        // the same snapshot RollbackRejectedFillsCore will receive.
        var makerOriginalFilled = buyMaker.AmountFilled;
        sellTaker.Fill(4);
        var wasRemoved = book.ApplyMakerFill(buyMaker, 4); // partial fill — not removed
        AssertEqual(false, wasRemoved, "buy maker should still be in the book after a 4/10 fill");

        var trade = MakeTrade(buyOrderId: 1001, sellOrderId: 1002, buyerId: 1, sellerId: 2,
                              stockId: 100, qty: 4, price: 5m);
        var matchResult = new MatchResult(
            Fills: new List<Transaction> { trade },
            TakerOriginalFilled: 0,
            MakerSnapshots: new List<MakerSnapshot>
            {
                new MakerSnapshot(buyMaker, makerOriginalFilled, wasRemoved),
            });
        var matches = new[] { (Taker: sellTaker, Match: matchResult) };

        // Validate-pass produces a RejectedFill keyed by the seller's order id. Here
        // the seller is the taker, so MakerOrderId is the sell taker — NOT in byMaker.
        var rejected = new[]
        {
            new RejectedFill(trade, MakerOrderId: 1002, Reason: "test: seller can't deliver"),
        };

        var accounts = new StubAccountsCache();
        var ordersById = new Dictionary<int, Order> { [1001] = buyMaker, [1002] = sellTaker };

        OrderExecutionService.RollbackRejectedFillsCore(
            matches, book, rejected, ordersById, accounts, new ReservationLedger(), logger, debugUserId: null);

        // Buy maker reverted, not cancelled, still on the book.
        AssertEqual(0, buyMaker.AmountFilled, "buy maker AmountFilled reverted to pre-fill");
        AssertEqual(Order.Statuses.Open, buyMaker.Status, "buy maker Status stays Open");
        AssertEqual(1001, book.PeekBestBuy()?.OrderId,
            "buy maker still resting at top of book");

        // Level qty restored to full 10 (matcher debited 4; rollback credits 4 back).
        var snapshot = book.Snapshot();
        var level = snapshot.Buys.Find(b => b.Price == 5m);
        AssertNotNull(level, "buy level @ $5 still exists");
        AssertEqual(10, level!.Quantity, "buy level qty credited back to full 10");

        // Sell taker reverted.
        AssertEqual(0, sellTaker.AmountFilled, "sell taker AmountFilled reverted");
        AssertEqual(Order.Statuses.Open, sellTaker.Status, "sell taker Status flipped back to Open");
    }

    // Same as scenario 1 but the matcher fully filled the buy maker and removed it
    // from the book. The rollback path must re-insert the maker with the rejected qty
    // back at the level — exactly the path that was leaking $100k+ of phantom
    // reservation per affected user before this fix.
    private static void SellTakerRejected_RevertsInnocentBuyMaker_FullyRemoved(ILogger logger)
    {
        var book = new OrderBook(stockId: 100, currency: CurrencyType.USD);

        var buyMaker = BuyLimit(orderId: 2001, userId: 1, stockId: 100, qty: 10, price: 5m);
        book.UpsertOrder(buyMaker);

        var sellTaker = SellLimit(orderId: 2002, userId: 2, stockId: 100, qty: 10, price: 5m);

        var makerOriginalFilled = buyMaker.AmountFilled;
        sellTaker.Fill(10);
        var wasRemoved = book.ApplyMakerFill(buyMaker, 10); // fully filled — removed
        AssertEqual(true, wasRemoved, "buy maker should be removed from book after 10/10 fill");
        AssertEqual(Order.Statuses.Filled, buyMaker.Status, "buy maker Status flipped to Filled by matcher");

        var trade = MakeTrade(buyOrderId: 2001, sellOrderId: 2002, buyerId: 1, sellerId: 2,
                              stockId: 100, qty: 10, price: 5m);
        var matchResult = new MatchResult(
            Fills: new List<Transaction> { trade },
            TakerOriginalFilled: 0,
            MakerSnapshots: new List<MakerSnapshot>
            {
                new MakerSnapshot(buyMaker, makerOriginalFilled, wasRemoved),
            });
        var matches = new[] { (Taker: sellTaker, Match: matchResult) };

        var rejected = new[]
        {
            new RejectedFill(trade, MakerOrderId: 2002, Reason: "test: seller can't deliver"),
        };

        var accounts = new StubAccountsCache();
        var ordersById = new Dictionary<int, Order> { [2001] = buyMaker, [2002] = sellTaker };

        OrderExecutionService.RollbackRejectedFillsCore(
            matches, book, rejected, ordersById, accounts, new ReservationLedger(), logger, debugUserId: null);

        // Buy maker reverted and re-inserted into the book.
        AssertEqual(0, buyMaker.AmountFilled, "buy maker AmountFilled reverted");
        AssertEqual(Order.Statuses.Open, buyMaker.Status, "buy maker Status flipped back to Open");
        AssertEqual(2001, book.PeekBestBuy()?.OrderId,
            "buy maker is re-inserted as best buy");

        var snapshot = book.Snapshot();
        var level = snapshot.Buys.Find(b => b.Price == 5m);
        AssertNotNull(level, "buy level @ $5 re-created after rollback");
        AssertEqual(10, level!.Quantity, "buy level qty back to full 10");

        // Sell taker reverted.
        AssertEqual(0, sellTaker.AmountFilled, "sell taker AmountFilled reverted");
        AssertEqual(Order.Statuses.Open, sellTaker.Status, "sell taker Status flipped back to Open");
    }

    // Regression: the existing seller-cancel path (buy taker hits sell maker, fill
    // rejected because that seller actually can't deliver) must still cancel the
    // seller maker, remove it from the book, AND release the maker's
    // Position.ReservedQuantity (Section 5a). The fix should not have disturbed it.
    private static void SellMakerRejected_StillCancelsAndReleases(ILogger logger)
    {
        var book = new OrderBook(stockId: 100, currency: CurrencyType.USD);

        // Sell maker resting on book: LimitSell 10 @ $5. Pre-seed Position so the 5a
        // release has something to drain, plus the per-order field so the post-fix
        // Section 5a (which reads from CurrentSellReservedQty) has a value to release.
        var sellMaker = SellLimit(orderId: 3001, userId: 1, stockId: 100, qty: 10, price: 5m);
        sellMaker.TakeSellReservation(10);
        book.UpsertOrder(sellMaker);

        var accounts = new StubAccountsCache();
        var pos = new Position { UserId = 1, StockId = 100, Quantity = 10, ReservedQuantity = 10 };
        accounts.Positions[(1, 100)] = pos;

        // Buy taker: LimitBuy 10 @ $5 — hits the seller fully.
        var buyTaker = BuyLimit(orderId: 3002, userId: 2, stockId: 100, qty: 10, price: 5m);

        var makerOriginalFilled = sellMaker.AmountFilled;
        buyTaker.Fill(10);
        var wasRemoved = book.ApplyMakerFill(sellMaker, 10);
        AssertEqual(true, wasRemoved, "sell maker fully filled, removed from book by matcher");

        var trade = MakeTrade(buyOrderId: 3002, sellOrderId: 3001, buyerId: 2, sellerId: 1,
                              stockId: 100, qty: 10, price: 5m);
        var matchResult = new MatchResult(
            Fills: new List<Transaction> { trade },
            TakerOriginalFilled: 0,
            MakerSnapshots: new List<MakerSnapshot>
            {
                new MakerSnapshot(sellMaker, makerOriginalFilled, wasRemoved),
            });
        var matches = new[] { (Taker: buyTaker, Match: matchResult) };

        // Seller-keyed rejection — points at the sell maker (id 3001), which IS in byMaker.
        var rejected = new[]
        {
            new RejectedFill(trade, MakerOrderId: 3001, Reason: "test: seller can't deliver"),
        };

        var ordersById = new Dictionary<int, Order> { [3001] = sellMaker, [3002] = buyTaker };

        OrderExecutionService.RollbackRejectedFillsCore(
            matches, book, rejected, ordersById, accounts, new ReservationLedger(), logger, debugUserId: null);

        // Sell maker cancelled and removed.
        AssertEqual(Order.Statuses.Cancelled, sellMaker.Status, "sell maker cancelled");
        AssertNull(book.PeekBestSell(), "no sell maker on the book after cancel");

        // Section 5a: Position.ReservedQuantity drained by maker.RemainingQuantity (= 10).
        AssertEqual(0, pos.ReservedQuantity, "Position.ReservedQuantity released by Section 5a");

        // Buy taker reverted.
        AssertEqual(0, buyTaker.AmountFilled, "buy taker AmountFilled reverted");
        AssertEqual(Order.Statuses.Open, buyTaker.Status, "buy taker Status flipped back to Open");
    }

    // -----------------------------------------------------------------------
    // Builders
    // -----------------------------------------------------------------------

    private static Order BuyLimit(int orderId, int userId, int stockId, int qty, decimal price)
    {
        var o = new Order
        {
            UserId = userId, StockId = stockId, Quantity = qty, Price = price,
            CurrencyType = CurrencyType.USD, OrderType = Order.Types.LimitBuy,
        };
        o.OrderId = orderId;
        return o;
    }

    private static Order SellLimit(int orderId, int userId, int stockId, int qty, decimal price)
    {
        var o = new Order
        {
            UserId = userId, StockId = stockId, Quantity = qty, Price = price,
            CurrencyType = CurrencyType.USD, OrderType = Order.Types.LimitSell,
        };
        o.OrderId = orderId;
        return o;
    }

    private static Transaction MakeTrade(int buyOrderId, int sellOrderId, int buyerId, int sellerId,
        int stockId, int qty, decimal price)
        => new()
        {
            StockId = stockId,
            BuyOrderId = buyOrderId,
            SellOrderId = sellOrderId,
            BuyerId = buyerId,
            SellerId = sellerId,
            Quantity = qty,
            Price = price,
            CurrencyType = CurrencyType.USD,
        };

    // -----------------------------------------------------------------------
    // Assertion helpers
    // -----------------------------------------------------------------------

    private static void AssertNull(object? actual, string message)
    {
        if (actual is not null) throw new InvalidOperationException($"{message} — expected null, got {actual}");
    }

    private static void AssertNotNull(object? actual, string message)
    {
        if (actual is null) throw new InvalidOperationException($"{message} — expected non-null, got null");
    }

    private static void AssertEqual<T>(T expected, T? actual, string message)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"{message} — expected {expected}, got {actual}");
    }
}

// =========================================================================
// Minimal IAccountsCache stub. Only GetPosition is used by RollbackRejectedFillsCore
// (Section 5a release). Everything else throws so a widening of the dependency
// would be noticed loudly.
// =========================================================================

internal sealed class StubAccountsCache : IAccountsCache
{
    public Dictionary<(int UserId, int StockId), Position> Positions { get; } = new();

    public Position? GetPosition(int userId, int stockId)
        => Positions.TryGetValue((userId, stockId), out var p) ? p : null;

    public Task EnsureLoadedAsync(IReadOnlyList<int> userIds, CancellationToken ct = default) => throw new NotImplementedException();
    public Task EnsureLoadedAsync(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Fund? GetFund(int userId, CurrencyType ccy) => throw new NotImplementedException();
    public void TrackNewPosition(Position pos) => throw new NotImplementedException();
    public ValueTask<IAsyncDisposable> AcquireFundGateAsync(int userId, CurrencyType ccy, CancellationToken ct = default) => throw new NotImplementedException();
    public ValueTask<IAsyncDisposable> AcquirePositionGateAsync(int userId, int stockId, CancellationToken ct = default) => throw new NotImplementedException();
    public ValueTask<IAsyncDisposable> AcquireUserGatesAsync(
        IReadOnlyCollection<(int UserId, CurrencyType Ccy)> fundKeys,
        IReadOnlyCollection<(int UserId, int StockId)> positionKeys,
        CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ReservationMismatch>> ReconcileReservationsAsync(
        bool clamp = false, CancellationToken ct = default) => throw new NotImplementedException();
    public void Clear() => throw new NotImplementedException();
}
