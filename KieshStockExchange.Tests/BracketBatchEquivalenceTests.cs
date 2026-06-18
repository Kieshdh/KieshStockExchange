using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Services.OtherServices;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// Round 2 §0005/§0013 equivalence + hardening tests for <see cref="OrderExecutionService.PlaceBracketBatchAsync"/>.
///
/// The batch route must produce the same per-triple outcome as the per-order
/// <see cref="OrderExecutionService.PlaceBracketAsync"/>: identical parent reservation + ledger
/// tuples, identical Attached child legs wired to their parent, partial-failure isolated to the bad
/// triple, and the cohort processed in SUBMISSION ORDER (== ascending aiUserId) so the bracket
/// registration + parent match order is deterministic. The submission-order guard pins the
/// determinism fix that replaced the Dictionary&lt;,&gt; walk (whose enumeration order is not
/// contractual) with a submission-ordered list.
///
/// The matching engine is stubbed to return NO fills, so each market parent reserves at
/// SettleOrderAsync then releases at the post-match CancelRemainder — the reservation LEDGER tuples
/// (the reserve event) are the equivalence oracle, exactly as the per-order path emits them. Fill-
/// level conservation is the soak's ConservationProbe/CK/Auditor job, not this unit's.
/// </summary>
public class BracketBatchEquivalenceTests
{
    private const int StockId = 10;

    // What a persisted bracket row must round-trip identically (OrderId excluded: both worlds assign
    // 1,2,… independently; ParentOrderId compared via the relative wiring asserts below).
    private sealed record RowSnap(string Status, OrderSide Side, EntryType Entry, StopKind Stop,
        decimal? StopPrice, int Quantity, decimal Price, bool HasParent);

    private static RowSnap Snap(Order o) => new(o.Status, o.Side, o.Entry, o.Stop, o.StopPrice,
        o.Quantity, o.Price, o.ParentOrderId.HasValue);

    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount, decimal Before, decimal After)> FundEntries = new();
        public readonly List<(string Action, decimal Amount, decimal BuyBefore, decimal BuyAfter,
            int SellBefore, int SellAfter)> OrderEntries = new();

        public HashSet<int> TrackedUserIds { get; } = new();
        public bool TrackAll { get; set; }
        public IReadOnlyList<LedgerEntry> Snapshot() => Array.Empty<LedgerEntry>();
        public int EntryCount => 0;
        public string SuggestedExportFileName => "test";
        public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
            decimal amount, decimal reservedBefore, decimal reservedAfter,
            decimal totalBefore, decimal totalAfter)
            => FundEntries.Add((action, amount, reservedBefore, reservedAfter));
        public void LogPosition(int userId, int stockId, int? orderId, string action,
            decimal amount, int reservedBefore, int reservedAfter,
            int quantityBefore, int quantityAfter) { }
        public void LogOrder(int userId, int orderId, string action, decimal amount,
            decimal buyReservationBefore, decimal buyReservationAfter,
            int sellReservedBefore, int sellReservedAfter)
            => OrderEntries.Add((action, amount, buyReservationBefore, buyReservationAfter,
                sellReservedBefore, sellReservedAfter));
        public void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
            int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount) { }
        public Task<string> ExportCsvAsync(string path, CancellationToken ct = default) => Task.FromResult(path);
        public string BuildCsv(CancellationToken ct = default) => string.Empty;
        public void Clear() { }
    }

    private sealed class World
    {
        public OrderExecutionService Engine = null!;
        public AccountsCache Accounts = null!;
        public RecordingLedger Ledger = null!;
        // Every Order row persisted (CreateOrder or bulk InsertAllAsync), snapshotted at insert time.
        public readonly List<RowSnap> InsertedRows = new();
        // OrderIds passed to the matching engine, in call order (parents matched in Phase 3).
        public readonly List<int> MatchedOrderIds = new();
        // parentOrderIds passed to RegisterBracket, in call order.
        public readonly List<int> RegisteredParents = new();
    }

    // One isolated engine stack: capturing db mock + real cache/settlement, stubbed match (no fills)
    // + a recording bracket coordinator. Each distinct userId gets a seeded 1,000,000 USD fund.
    private static World NewWorld()
    {
        var w = new World();
        var funds = new Dictionary<int, Fund>();

        Fund FundFor(int userId)
        {
            if (!funds.TryGetValue(userId, out var f))
            {
                f = new Fund { UserId = userId, CurrencyType = CurrencyType.USD, TotalBalance = 1_000_000m, ReservedBalance = 0m };
                funds[userId] = f;
            }
            return f;
        }

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) => ids.Select(FundFor).ToList());
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Position>());
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order>());

        int nextId = 1;
        db.Setup(d => d.CreateOrder(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
          .Callback<Order, CancellationToken>((o, _) => { o.OrderId = nextId++; w.InsertedRows.Add(Snap(o)); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<Order>, CancellationToken>((items, _) =>
          { foreach (var o in items) { o.OrderId = nextId++; w.InsertedRows.Add(Snap(o)); } })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Mock<ITransaction>().Object);

        var registry = new OrderRegistry();
        w.Ledger = new RecordingLedger();
        w.Accounts = new AccountsCache(db.Object, registry, w.Ledger, NullLogger<AccountsCache>.Instance);

        var stocks = new Mock<IStockService>();
        Stock? stockOut = new Stock();
        stocks.Setup(s => s.TryGetById(It.IsAny<int>(), out stockOut)).Returns(true);
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(true);
        var validator = new OrderValidator(stocks.Object);

        var settlement = new SettlementEngine(db.Object, w.Accounts, w.Ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));

        // Book lock: invoke the body against a fresh in-memory book (no resting liquidity → no fills).
        var books = new Mock<IOrderBookEngine>();
        books.Setup(b => b.WithBookLockAsync(It.IsAny<int>(), It.IsAny<CurrencyType>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<OrderBook, Task>>()))
             .Returns<int, CurrencyType, CancellationToken, Func<OrderBook, Task>>(
                 (sid, ccy, _, body) => body(new OrderBook(sid, ccy)));

        // Matching: record taker order id, return NO fills (parent stays open → CancelRemainder).
        var matching = new Mock<IMatchingEngine>();
        matching.Setup(m => m.Match(It.IsAny<Order>(), It.IsAny<OrderBook>(),
                It.IsAny<CancellationToken>(), It.IsAny<TradeBatchScope?>()))
            .Returns<Order, OrderBook, CancellationToken, TradeBatchScope?>((taker, _, __, ___) =>
            {
                w.MatchedOrderIds.Add(taker.OrderId);
                return new MatchResult(new List<Transaction>(), taker.AmountFilled, new List<MakerSnapshot>());
            });

        // Bracket coordinator: record registration order; IsBracketParent unused (no fills).
        var bracket = new Mock<IBracketCoordinator>();
        bracket.Setup(b => b.RegisterBracket(It.IsAny<int>()))
               .Callback<int>(pid => w.RegisteredParents.Add(pid));

        var config = new Mock<IConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns(Mock.Of<IConfigurationSection>());

        w.Engine = new OrderExecutionService(
            db.Object,
            books.Object,
            matching.Object,
            validator,
            settlement,
            new Mock<IMarketDataService>().Object,
            w.Accounts,
            new Mock<IOrderCacheService>().Object,
            w.Ledger,
            registry,
            new Mock<IServerNotificationService>().Object,
            bracket.Object,
            config.Object,
            NullLogger<OrderExecutionService>.Instance);
        return w;
    }

    // A long-bracket triple as the bot fleet builds it. The parent is a LIMIT buy (deterministic
    // qty×price fund reservation, rests unfilled against the empty book — no live-price dependency
    // and no cancel-remainder), an uncapped stop-market sell SL covering the whole qty, and one
    // limit-sell take-profit. A limit entry exercises the same reserve→insert→register→match path
    // as a market entry for the purposes of these batch-equivalence asserts.
    private static (Order Parent, Order? Sl, IReadOnlyList<Order> Tps) Triple(int userId, int qty = 10)
    {
        var parent = new Order
        {
            UserId = userId, StockId = StockId, Quantity = qty, Price = 100m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        var sl = new Order
        {
            UserId = userId, StockId = StockId, Quantity = qty, Price = 0m, StopPrice = 90m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.Stop,
        };
        var tp = new Order
        {
            UserId = userId, StockId = StockId, Quantity = qty, Price = 120m,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        return (parent, sl, new[] { tp });
    }

    // Both reservation-ledger tuples and inserted rows are compared as MULTISETS (sorted): the
    // per-order path interleaves parent+legs+reserve/release per bracket, while the batch path does
    // all parents then all legs then all matches — same content, different emission order. Each
    // bracket is a DISTINCT user, so per-user before/after values are interleaving-independent.
    private static List<(decimal, decimal, decimal)> FundTuples(RecordingLedger l)
        => l.FundEntries.Select(e => (e.Amount, e.Before, e.After))
            .OrderBy(t => t.Amount).ThenBy(t => t.Before).ThenBy(t => t.After).ToList();

    private static List<RowSnap> SortedRows(IEnumerable<RowSnap> rows)
        => rows.OrderBy(r => r.HasParent).ThenBy(r => (int)r.Side).ThenBy(r => (int)r.Entry)
            .ThenBy(r => (int)r.Stop).ThenBy(r => r.Quantity).ThenBy(r => r.Price)
            .ThenBy(r => r.StopPrice ?? 0m).ThenBy(r => r.Status).ToList();

    [Fact]
    public async Task Batched_bracket_matches_per_order_rows_and_reservation_ledger()
    {
        var a = NewWorld();
        var b = NewWorld();

        // World A: per-order PlaceBracketAsync (the proven path) for two users.
        var (pa1, sla1, tpa1) = Triple(userId: 1);
        var (pa2, sla2, tpa2) = Triple(userId: 2);
        var ra1 = await a.Engine.PlaceBracketAsync(pa1, sla1, tpa1);
        var ra2 = await a.Engine.PlaceBracketAsync(pa2, sla2, tpa2);
        Assert.True(ra1.PlacedSuccessfully);
        Assert.True(ra2.PlacedSuccessfully);

        // World B: the same two brackets through the batch route.
        var (pb1, slb1, tpb1) = Triple(userId: 1);
        var (pb2, slb2, tpb2) = Triple(userId: 2);
        var rb = await b.Engine.PlaceBracketBatchAsync(new (Order, Order?, IReadOnlyList<Order>)[]
        {
            (pb1, slb1, tpb1),
            (pb2, slb2, tpb2),
        });
        Assert.True(rb[0].PlacedSuccessfully);
        Assert.True(rb[1].PlacedSuccessfully);

        // Identical persisted rows (parent + Attached legs, same schema columns) as a multiset.
        Assert.Equal(SortedRows(a.InsertedRows), SortedRows(b.InsertedRows));

        // Both legs of each bracket persisted as dormant children pointing at a parent.
        Assert.Equal(4, b.InsertedRows.Count(r => r.HasParent)); // 2 brackets × (SL + 1 TP)
        Assert.All(b.InsertedRows.Where(r => r.HasParent),
            r => Assert.Equal(Order.Statuses.Attached, r.Status));

        // Identical reservation ledger amount tuples (reason labels may differ by route).
        Assert.Equal(FundTuples(a.Ledger), FundTuples(b.Ledger));

        // Each batched leg carries its own parent's id + Attached status (wiring intact).
        Assert.Equal(pb1.OrderId, slb1!.ParentOrderId!.Value);
        Assert.Equal(pb1.OrderId, tpb1[0].ParentOrderId!.Value);
        Assert.Equal(pb2.OrderId, slb2!.ParentOrderId!.Value);
        Assert.Equal(Order.Statuses.Attached, slb1.Status);
        Assert.Equal(Order.Statuses.Attached, tpb1[0].Status);
    }

    [Fact]
    public async Task Batch_processes_cohort_in_submission_order()
    {
        // Three brackets submitted ascending aiUserId. The determinism contract: parents register +
        // match in submission order. Parent ids are assigned 1,2,3 in Phase 1, so submission order
        // == ascending parent id; the fix guarantees the Phase 2/3 walk follows it rather than a
        // Dictionary enumeration whose order is not contractual.
        var w = NewWorld();
        var (p1, s1, t1) = Triple(userId: 1);
        var (p2, s2, t2) = Triple(userId: 2);
        var (p3, s3, t3) = Triple(userId: 3);

        var rs = await w.Engine.PlaceBracketBatchAsync(new (Order, Order?, IReadOnlyList<Order>)[]
        {
            (p1, s1, t1), (p2, s2, t2), (p3, s3, t3),
        });

        Assert.All(rs, r => Assert.True(r.PlacedSuccessfully));
        Assert.Equal(new[] { p1.OrderId, p2.OrderId, p3.OrderId }, w.RegisteredParents);
        Assert.Equal(new[] { p1.OrderId, p2.OrderId, p3.OrderId }, w.MatchedOrderIds);
        // Ascending by construction (Phase 1 assigns ids in submission order).
        Assert.Equal(w.RegisteredParents.OrderBy(x => x).ToList(), w.RegisteredParents);
    }

    [Fact]
    public async Task Partial_failure_rejects_only_the_bad_triple()
    {
        var w = NewWorld();
        var good1 = Triple(userId: 1);
        var bad = Triple(userId: 2);
        bad.Parent.Quantity = 0; // ValidateNew rejects: "Quantity must be positive."
        var good2 = Triple(userId: 3);

        var rs = await w.Engine.PlaceBracketBatchAsync(new (Order, Order?, IReadOnlyList<Order>)[]
        {
            good1, bad, good2,
        });

        Assert.True(rs[0].PlacedSuccessfully);
        Assert.False(rs[1].PlacedSuccessfully);
        Assert.True(rs[2].PlacedSuccessfully);

        // Only the two survivors registered + matched, in submission order; the reject did neither.
        Assert.Equal(new[] { good1.Parent.OrderId, good2.Parent.OrderId }, w.RegisteredParents);
        Assert.Equal(new[] { good1.Parent.OrderId, good2.Parent.OrderId }, w.MatchedOrderIds);
        // The bad parent never got an OrderId (never reserved/inserted).
        Assert.False(bad.Parent.OrderId > 0);
    }
}
