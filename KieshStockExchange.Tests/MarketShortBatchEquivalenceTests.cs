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
/// Round 2 §0005 equivalence tests for <see cref="OrderExecutionService.PlaceMarketShortBatchAsync"/>.
/// The batched flat-only market-short route is a structural cohort entry point that loops the proven
/// per-order PlaceAndMatchAsync; this pins that the wrapper preserves it exactly — one result per
/// order in submission order, identical persisted rows + reservation ledger tuples, partial failure
/// isolated to the bad order, and the cohort matched in submission order (ascending aiUserId).
///
/// Matching is stubbed to return no fills (no resting liquidity), so the comparison is route-vs-route
/// at the placement/reservation boundary — robust to short-open settlement semantics because BOTH
/// worlds hit the same behaviour. Fill-level short-collateral conservation is the soak's job.
/// </summary>
public class MarketShortBatchEquivalenceTests
{
    private const int StockId = 10;

    private sealed record RowSnap(string Status, OrderSide Side, EntryType Entry, StopKind Stop,
        int Quantity, decimal Price, decimal? BuyBudget);

    private static RowSnap Snap(Order o) => new(o.Status, o.Side, o.Entry, o.Stop, o.Quantity, o.Price, o.BuyBudget);

    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount, decimal Before, decimal After)> FundEntries = new();
        public readonly List<(string Action, decimal Amount, int Before, int After)> PositionEntries = new();

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
            int quantityBefore, int quantityAfter)
            => PositionEntries.Add((action, amount, reservedBefore, reservedAfter));
        public void LogOrder(int userId, int orderId, string action, decimal amount,
            decimal buyReservationBefore, decimal buyReservationAfter,
            int sellReservedBefore, int sellReservedAfter) { }
        public void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
            int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount) { }
        public Task<string> ExportCsvAsync(string path, CancellationToken ct = default) => Task.FromResult(path);
        public string BuildCsv(CancellationToken ct = default) => string.Empty;
        public void Clear() { }
    }

    private sealed class World
    {
        public OrderExecutionService Engine = null!;
        public RecordingLedger Ledger = null!;
        public readonly List<RowSnap> InsertedRows = new();
        public readonly List<int> MatchedOrderIds = new();
    }

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

        var positions = new Dictionary<int, Position>();
        Position PosFor(int userId)
        {
            if (!positions.TryGetValue(userId, out var p))
            {
                // Seed inventory so each sell reserves shares and settles deterministically; the
                // flat-short collateral path is settlement-level and is covered by the soak, not here.
                p = new Position { UserId = userId, StockId = StockId, Quantity = 100 };
                positions[userId] = p;
            }
            return p;
        }

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) => ids.Select(FundFor).ToList());
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) => ids.Select(PosFor).ToList());
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order>());

        int nextId = 1;
        var byId = new Dictionary<int, Order>();
        db.Setup(d => d.CreateOrder(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
          .Callback<Order, CancellationToken>((o, _) => { o.OrderId = nextId++; byId[o.OrderId] = o; w.InsertedRows.Add(Snap(o)); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<Order>, CancellationToken>((items, _) =>
          { foreach (var o in items) { o.OrderId = nextId++; byId[o.OrderId] = o; w.InsertedRows.Add(Snap(o)); } })
          .Returns(Task.CompletedTask);
        // An unfilled market short's remainder routes through OrderCanceller, which does
        // `_db.GetOrderById(...) ?? throw`. Mirror the real DB by returning the inserted row.
        db.Setup(d => d.GetOrderById(It.IsAny<int>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((int id, CancellationToken _) => byId.TryGetValue(id, out var o) ? o : null);
        db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Mock<ITransaction>().Object);

        var registry = new OrderRegistry();
        w.Ledger = new RecordingLedger();
        var accounts = new AccountsCache(db.Object, registry, w.Ledger, NullLogger<AccountsCache>.Instance);

        var stocks = new Mock<IStockService>();
        Stock? stockOut = new Stock();
        stocks.Setup(s => s.TryGetById(It.IsAny<int>(), out stockOut)).Returns(true);
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(true);
        var validator = new OrderValidator(stocks.Object);

        var settlement = new SettlementEngine(db.Object, accounts, w.Ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));

        var books = new Mock<IOrderBookEngine>();
        books.Setup(b => b.WithBookLockAsync(It.IsAny<int>(), It.IsAny<CurrencyType>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<OrderBook, Task>>()))
             .Returns<int, CurrencyType, CancellationToken, Func<OrderBook, Task>>(
                 (sid, ccy, _, body) => body(new OrderBook(sid, ccy)));

        var matching = new Mock<IMatchingEngine>();
        matching.Setup(m => m.Match(It.IsAny<Order>(), It.IsAny<OrderBook>(),
                It.IsAny<CancellationToken>(), It.IsAny<TradeBatchScope?>()))
            .Returns<Order, OrderBook, CancellationToken, TradeBatchScope?>((taker, _, __, ___) =>
            {
                w.MatchedOrderIds.Add(taker.OrderId);
                return new MatchResult(new List<Transaction>(), taker.AmountFilled, new List<MakerSnapshot>());
            });

        var config = new Mock<IConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns(Mock.Of<IConfigurationSection>());

        w.Engine = new OrderExecutionService(
            db.Object,
            books.Object,
            matching.Object,
            validator,
            settlement,
            new Mock<IMarketDataService>().Object,
            accounts,
            new Mock<IOrderCacheService>().Object,
            w.Ledger,
            registry,
            new Mock<IServerNotificationService>().Object,
            new Mock<IBracketCoordinator>().Object,
            config.Object,
            NullLogger<OrderExecutionService>.Instance);
        return w;
    }

    private static Order ShortSell(int userId, int qty = 10) => new()
    {
        UserId = userId, StockId = StockId, Quantity = qty, Price = 0m,
        CurrencyType = CurrencyType.USD, Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
    };

    private static List<RowSnap> Sorted(IEnumerable<RowSnap> rows)
        => rows.OrderBy(r => r.Quantity).ThenBy(r => r.Price).ThenBy(r => (int)r.Side)
            .ThenBy(r => r.Status).ToList();

    [Fact]
    public async Task Batched_short_matches_per_order_results_rows_and_ledger()
    {
        var a = NewWorld();
        var b = NewWorld();

        // World A: per-order PlaceAndMatchAsync (what PlaceTrueMarketSellOrderAsync resolves to).
        var ra1 = await a.Engine.PlaceAndMatchAsync(ShortSell(userId: 1));
        var ra2 = await a.Engine.PlaceAndMatchAsync(ShortSell(userId: 2));

        // World B: the same two short opens through the batch route.
        var rb = await b.Engine.PlaceMarketShortBatchAsync(new[] { ShortSell(userId: 1), ShortSell(userId: 2) });

        Assert.Equal(ra1.PlacedSuccessfully, rb[0].PlacedSuccessfully);
        Assert.Equal(ra2.PlacedSuccessfully, rb[1].PlacedSuccessfully);

        // Identical persisted rows and reservation ledger tuples (multisets — distinct users).
        Assert.Equal(Sorted(a.InsertedRows), Sorted(b.InsertedRows));
        Assert.Equal(
            a.Ledger.FundEntries.Select(e => (e.Amount, e.Before, e.After)).OrderBy(t => t.Amount).ToList(),
            b.Ledger.FundEntries.Select(e => (e.Amount, e.Before, e.After)).OrderBy(t => t.Amount).ToList());
        Assert.Equal(
            a.Ledger.PositionEntries.Select(e => (e.Amount, e.Before, e.After)).OrderBy(t => t.Amount).ToList(),
            b.Ledger.PositionEntries.Select(e => (e.Amount, e.Before, e.After)).OrderBy(t => t.Amount).ToList());

        // Cohort matched in submission order (ascending aiUserId).
        Assert.Equal(b.MatchedOrderIds.OrderBy(x => x).ToList(), b.MatchedOrderIds);
    }

    [Fact]
    public async Task Partial_failure_rejects_individual_and_keeps_the_rest()
    {
        var w = NewWorld();
        var ok1 = ShortSell(userId: 1, qty: 10);
        var bad = ShortSell(userId: 2, qty: 0); // ValidateNew rejects: "Quantity must be positive."
        var ok2 = ShortSell(userId: 3, qty: 5);

        var rs = await w.Engine.PlaceMarketShortBatchAsync(new[] { ok1, bad, ok2 });

        Assert.False(rs[1].PlacedSuccessfully);
        Assert.Contains("Quantity", rs[1].ErrorMessage);
        // The reject never reached the matcher; the two survivors were processed in submission order.
        Assert.DoesNotContain(bad.OrderId, w.MatchedOrderIds);
        Assert.False(bad.OrderId > 0); // never inserted
        Assert.Equal(2, w.MatchedOrderIds.Count);
    }
}
