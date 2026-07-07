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
/// §C group-commit equivalence for the SAME-USER CROSS-LISTED case — the shared-<c>Position</c> shape the
/// baseline <c>GroupCommitEquivalenceTests</c> deliberately avoids (it uses DISTINCT users per currency, so
/// no Position is shared). Here ONE user holds both the USD and the EUR book of a cross-listed stock (the
/// same currency-agnostic Position row) AND chains two same-currency reservations, so the per-currency
/// before/after ledger sequence is order-sensitive. Under group-commit USD groups run in one shard and EUR
/// in another; within a currency the order is preserved, so OFF vs ON must still be byte-identical.
///
/// NOTE: the matcher is mocked to return NO fills (as in the baseline), so the buys reserve funds and rest;
/// no Position QUANTITY mutation occurs, hence the position-ledger oracle is (validly) empty here. It is
/// captured anyway so this fixture upgrades directly into the FILL-level shared-Position race test once a
/// fills-producing matcher + a multi-connection row-lock DB fake are added (see the implementation handoff,
/// Patch C task (ii)) — at which point LogPosition tuples become the share-conservation oracle.
/// </summary>
public class GroupCommitSharedPositionEquivalenceTests
{
    private sealed record RowSnap(string Status, OrderSide Side, EntryType Entry, StopKind Stop,
        int StockId, int Quantity, decimal Price, CurrencyType Currency);

    private static RowSnap Snap(Order o) => new(o.Status, o.Side, o.Entry, o.Stop,
        o.StockId, o.Quantity, o.Price, o.CurrencyType);

    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount, decimal Before, decimal After)> FundEntries = new();
        // §C share-conservation oracle: capture the position-ledger tuples the baseline discards.
        public readonly List<(string Action, decimal Amount, int QtyBefore, int QtyAfter)> PositionEntries = new();

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
            => PositionEntries.Add((action, amount, quantityBefore, quantityAfter));
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
    }

    private static World NewWorld(bool groupCommit)
    {
        var w = new World();
        var funds = new Dictionary<(int, CurrencyType), Fund>();

        Fund FundFor(int userId, CurrencyType ccy)
        {
            if (!funds.TryGetValue((userId, ccy), out var f))
            {
                f = new Fund { UserId = userId, CurrencyType = ccy, TotalBalance = 1_000_000m, ReservedBalance = 0m };
                funds[(userId, ccy)] = f;
            }
            return f;
        }

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) =>
              ids.SelectMany(id => new[] { FundFor(id, CurrencyType.USD), FundFor(id, CurrencyType.EUR) }).ToList());
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
        db.Setup(d => d.RunInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
          .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));

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
                new MatchResult(new List<Transaction>(), taker.AmountFilled, new List<MakerSnapshot>()));

        var config = new Mock<IConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns(Mock.Of<IConfigurationSection>());
        if (groupCommit)
        {
            var onSection = new Mock<IConfigurationSection>();
            onSection.Setup(s => s.Value).Returns("true");
            config.Setup(c => c.GetSection("Db:GroupCommit:Enabled")).Returns(onSection.Object);
        }

        w.Engine = new OrderExecutionService(
            db.Object, books.Object, matching.Object, validator, settlement,
            new Mock<IMarketDataService>().Object, accounts, new Mock<IOrderCacheService>().Object,
            w.Ledger, registry, new Mock<IServerNotificationService>().Object,
            new Mock<IBracketCoordinator>().Object, config.Object,
            NullLogger<OrderExecutionService>.Instance);
        return w;
    }

    private static Order Buy(int userId, int stockId, CurrencyType ccy, int qty = 10, decimal price = 100m)
        => new()
        {
            UserId = userId, StockId = stockId, Quantity = qty, Price = price,
            CurrencyType = ccy, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };

    // ONE user (1) across BOTH books of cross-listed stock 10 (shared Position (1,10)) PLUS a second
    // stock in each currency so user 1's USD fund and EUR fund each chain two reservations (order-sensitive
    // before/after). USD groups: (10,USD),(11,USD); EUR groups: (10,EUR),(11,EUR).
    private static IReadOnlyList<Order> SameUserCrossListedBatch() => new[]
    {
        Buy(userId: 1, stockId: 10, CurrencyType.USD),
        Buy(userId: 1, stockId: 11, CurrencyType.USD, qty: 7, price: 120m),
        Buy(userId: 1, stockId: 10, CurrencyType.EUR),
        Buy(userId: 1, stockId: 11, CurrencyType.EUR, qty: 5, price: 90m),
    };

    private static List<(decimal, decimal, decimal)> FundTuples(RecordingLedger l)
        => l.FundEntries.Select(e => (e.Amount, e.Before, e.After))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ToList();

    private static List<(string, decimal, int, int)> PositionTuples(RecordingLedger l)
        => l.PositionEntries.Select(e => (e.Action, e.Amount, e.QtyBefore, e.QtyAfter))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ThenBy(t => t.Item4).ToList();

    private static List<RowSnap> SortedRows(IEnumerable<RowSnap> rows)
        => rows.OrderBy(r => (int)r.Currency).ThenBy(r => r.StockId).ThenBy(r => (int)r.Side)
            .ThenBy(r => (int)r.Entry).ThenBy(r => r.Quantity).ThenBy(r => r.Price)
            .ThenBy(r => r.Status).ToList();

    [Fact]
    public async Task GroupCommit_off_vs_on_sameUserCrossListed_isIdentical()
    {
        var off = NewWorld(groupCommit: false);
        var on = NewWorld(groupCommit: true);

        var offResults = await off.Engine.PlaceAndMatchBatchAsync(SameUserCrossListedBatch());
        var onResults = await on.Engine.PlaceAndMatchBatchAsync(SameUserCrossListedBatch());

        Assert.All(offResults, r => Assert.True(r.PlacedSuccessfully));
        Assert.All(onResults, r => Assert.True(r.PlacedSuccessfully));

        // Persisted rows + per-currency reservation-ledger tuples are identical OFF vs ON even when one user
        // holds the shared cross-listed Position and chains same-currency reservations.
        Assert.Equal(SortedRows(off.InsertedRows), SortedRows(on.InsertedRows));
        Assert.Equal(FundTuples(off.Ledger), FundTuples(on.Ledger));
        // Share-conservation oracle: identical OFF vs ON (empty in the no-fill path; the assertion is the
        // seam the fill-level shared-Position race test extends — see the class remarks).
        Assert.Equal(PositionTuples(off.Ledger), PositionTuples(on.Ledger));
    }
}
