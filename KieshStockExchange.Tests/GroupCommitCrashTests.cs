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
/// §group-commit crash-window contract. With <c>Db:GroupCommit:Enabled</c>, a currency's groups commit
/// under ONE root tx (one fsync) per chunk. The hazard this round introduces: a process death between a
/// savepoint release and the chunk's root commit loses that whole chunk — and <c>ConservationProbe</c>
/// reads the cache, so it is BLIND to a durable shortfall. This test kills the chunk's root commit (the
/// fsync) and reconciles the DURABLE store ALONE (not the cache): the surviving rows must be internally
/// conserved, the crashed chunk must leave nothing durable, and the engine must restore the crashed
/// chunk's cache + recover its orders so cache == DB.
///
/// Determinism: a single currency with <c>MaxBatch=1</c> makes each group its own chunk processed
/// SEQUENTIALLY, so "chunk 1 commits, chunk 2 crashes" is reproducible (no parallel-shard race). The
/// fake DB below mirrors PgDBService's ambient/savepoint nesting (AsyncLocal frame stack): writes flush
/// to the durable store only on a ROOT commit; a savepoint release merges into its parent; a rollback or
/// an injected crash discards the frame. Fill-level money/share conservation under load is the soak's
/// job (ConservationProbe/CK/ReservationAuditor); here the oracle is the durable-vs-cache reconciliation.
/// </summary>
public class GroupCommitCrashTests
{
    private const decimal StartBalance = 1_000_000m;

    // ── A fake IDataBaseService that models real durability: writes are durable only after a ROOT
    //    commit. Mirrors PgDBService's AsyncLocal ambient (root tx) + nested SAVEPOINT semantics.
    private sealed class FakeDb
    {
        internal sealed class Frame { public List<Action> Pending = new(); public Frame? Parent; public bool IsRoot; }
        private readonly AsyncLocal<Frame?> _ambient = new();

        // Durable (committed) snapshots — the "DB alone" view a crash recovery would cold-load from.
        public readonly Dictionary<int, string> Orders = new();                              // orderId → Status
        public readonly Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)> Funds = new();
        public readonly Dictionary<(int, int), (int Qty, int Reserved)> Positions = new();
        public readonly List<Transaction> Transactions = new();

        public int RootCommits;
        public int? CrashOnRootCommit;   // 1-based index of the root commit to fail (the simulated fsync death)
        private int _rootSeen;
        public int NextId = 1;

        // Seed funds so limit buys have budget; both currencies for every requested user.
        public readonly Dictionary<(int, CurrencyType), Fund> Seed = new();

        public ITransaction Begin()
        {
            var f = new Frame { Parent = _ambient.Value, IsRoot = _ambient.Value is null };
            _ambient.Value = f;
            return new FrameTx(this, f);
        }

        private void Stage(Action apply)
        {
            var f = _ambient.Value;
            if (f is null) { apply(); return; } // no ambient ⇒ autocommit (not used on the group path)
            f.Pending.Add(apply);
        }

        public void StageOrder(Order o) { int id = o.OrderId; string st = o.Status; Stage(() => Orders[id] = st); }
        public void StageFund(Fund x) { var k = (x.UserId, x.CurrencyType); var v = (x.TotalBalance, x.ReservedBalance); Stage(() => Funds[k] = v); }
        public void StagePosition(Position p) { var k = (p.UserId, p.StockId); var v = (p.Quantity, p.ReservedQuantity); Stage(() => Positions[k] = v); }
        public void StageTransaction(Transaction t) { Stage(() => Transactions.Add(t)); }

        internal void Commit(Frame f)
        {
            if (f.IsRoot)
            {
                _rootSeen++;
                if (CrashOnRootCommit == _rootSeen)
                {
                    _ambient.Value = f.Parent;                 // clear ambient like a dropped connection
                    throw new InvalidOperationException("simulated crash before fsync");
                }
                foreach (var a in f.Pending) a();              // the durable flush (one fsync)
                RootCommits++;
            }
            else
            {
                f.Parent!.Pending.AddRange(f.Pending);         // RELEASE SAVEPOINT: merge into parent
            }
            _ambient.Value = f.Parent;
        }

        internal void Rollback(Frame f) => _ambient.Value = f.Parent;   // discard this frame's pending

        private sealed class FrameTx : ITransaction
        {
            private readonly FakeDb _db; private readonly Frame _f; private bool _done;
            public FrameTx(FakeDb db, Frame f) { _db = db; _f = f; }
            public bool IsRoot => _f.IsRoot;
            public ValueTask CommitAsync(CancellationToken ct = default) { if (!_done) { _done = true; _db.Commit(_f); } return ValueTask.CompletedTask; }
            public ValueTask RollbackAsync(CancellationToken ct = default) { if (!_done) { _done = true; _db.Rollback(_f); } return ValueTask.CompletedTask; }
            public ValueTask DisposeAsync() { if (!_done) { _done = true; _db.Rollback(_f); } return ValueTask.CompletedTask; }
        }
    }

    private sealed class World
    {
        public OrderExecutionService Engine = null!;
        public AccountsCache Accounts = null!;
        public FakeDb Db = null!;
    }

    private static World NewWorld(int maxBatch, int? crashOnRootCommit)
    {
        var w = new World();
        var fake = new FakeDb { CrashOnRootCommit = crashOnRootCommit };
        w.Db = fake;

        Fund FundFor(int userId, CurrencyType ccy)
        {
            if (!fake.Seed.TryGetValue((userId, ccy), out var f))
            {
                f = new Fund { UserId = userId, CurrencyType = ccy, TotalBalance = StartBalance, ReservedBalance = 0m };
                fake.Seed[(userId, ccy)] = f;
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

        // Transaction nesting (root tx + savepoints) routed to the durability fake.
        db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
          .Returns((CancellationToken _) => Task.FromResult(fake.Begin()));
        db.Setup(d => d.RunInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
          .Returns<Func<CancellationToken, Task>, CancellationToken>(async (action, ct) =>
          {
              var tx = fake.Begin();
              try { await action(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
              catch { await tx.RollbackAsync(ct).ConfigureAwait(false); throw; }
          });

        // Writes stage into the current frame; durable only when the enclosing ROOT commits.
        db.Setup(d => d.CreateOrder(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
          .Returns((Order o, CancellationToken _) => { o.OrderId = fake.NextId++; fake.StageOrder(o); return Task.CompletedTask; });
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Returns((IEnumerable<Order> items, CancellationToken _) =>
          { foreach (var o in items) { o.OrderId = fake.NextId++; fake.StageOrder(o); } return Task.CompletedTask; });
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Returns((IEnumerable<Order> items, CancellationToken _) => { foreach (var o in items) fake.StageOrder(o); return Task.CompletedTask; });
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Fund>>(), It.IsAny<CancellationToken>()))
          .Returns((IEnumerable<Fund> items, CancellationToken _) => { foreach (var x in items) fake.StageFund(x); return Task.CompletedTask; });
        db.Setup(d => d.UpdateAllAsync(It.IsAny<IEnumerable<Position>>(), It.IsAny<CancellationToken>()))
          .Returns((IEnumerable<Position> items, CancellationToken _) => { foreach (var p in items) fake.StagePosition(p); return Task.CompletedTask; });
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Position>>(), It.IsAny<CancellationToken>()))
          .Returns((IEnumerable<Position> items, CancellationToken _) => { foreach (var p in items) fake.StagePosition(p); return Task.CompletedTask; });
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Transaction>>(), It.IsAny<CancellationToken>()))
          .Returns((IEnumerable<Transaction> items, CancellationToken _) => { foreach (var t in items) fake.StageTransaction(t); return Task.CompletedTask; });

        var registry = new OrderRegistry();
        var ledger = new NullReservationLedger();
        w.Accounts = new AccountsCache(db.Object, registry, ledger, NullLogger<AccountsCache>.Instance);

        var stocks = new Mock<IStockService>();
        Stock? stockOut = new Stock();
        stocks.Setup(s => s.TryGetById(It.IsAny<int>(), out stockOut)).Returns(true);
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(true);
        var validator = new OrderValidator(stocks.Object);

        var settlement = new SettlementEngine(db.Object, w.Accounts, ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));

        var books = new Mock<IOrderBookEngine>();
        books.Setup(b => b.WithBookLockAsync(It.IsAny<int>(), It.IsAny<CurrencyType>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<OrderBook, Task>>()))
             .Returns<int, CurrencyType, CancellationToken, Func<OrderBook, Task>>(
                 (sid, ccy, _, body) => body(new OrderBook(sid, ccy)));

        // No fills: each limit buy reserves at the pre-check and rests. The crash hazard under test is
        // the chunk root commit, not fill settlement — so an empty match keeps the scenario deterministic.
        var matching = new Mock<IMatchingEngine>();
        matching.Setup(m => m.Match(It.IsAny<Order>(), It.IsAny<OrderBook>(),
                It.IsAny<CancellationToken>(), It.IsAny<TradeBatchScope?>()))
            .Returns<Order, OrderBook, CancellationToken, TradeBatchScope?>((taker, _, __, ___) =>
                new MatchResult(new List<Transaction>(), taker.AmountFilled, new List<MakerSnapshot>()));

        var config = new Mock<IConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns(Mock.Of<IConfigurationSection>());
        void SetKey(string key, string val)
        {
            var sec = new Mock<IConfigurationSection>(); sec.Setup(s => s.Value).Returns(val);
            config.Setup(c => c.GetSection(key)).Returns(sec.Object);
        }
        SetKey("Db:GroupCommit:Enabled", "true");
        SetKey("Db:GroupCommit:MaxBatch", maxBatch.ToString());

        w.Engine = new OrderExecutionService(
            db.Object, books.Object, matching.Object, validator, settlement,
            new Mock<IMarketDataService>().Object, w.Accounts, new Mock<IOrderCacheService>().Object,
            ledger, registry, new Mock<IServerNotificationService>().Object,
            new Mock<IBracketCoordinator>().Object, config.Object,
            NullLogger<OrderExecutionService>.Instance);
        return w;
    }

    private static Order Buy(int userId, int stockId, int qty = 10, decimal price = 100m)
        => new()
        {
            UserId = userId, StockId = stockId, Quantity = qty, Price = price,
            CurrencyType = CurrencyType.USD, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };

    [Fact]
    public async Task Crash_on_a_chunk_commit_leaves_durable_DB_conserved_and_recovers_the_chunk()
    {
        // One USD shard, MaxBatch=1 ⇒ two groups = two sequential chunks. Root commits in order:
        //   #1 Phase-2 order insert, #2 chunk-1 (stock 10), #3 chunk-2 (stock 11) ← crash here.
        var w = NewWorld(maxBatch: 1, crashOnRootCommit: 3);

        var orders = new[] { Buy(userId: 1, stockId: 10), Buy(userId: 2, stockId: 11) };
        var results = await w.Engine.PlaceAndMatchBatchAsync(orders);

        // Chunk 1 committed; chunk 2's commit died. The engine must have recovered chunk 2's order and
        // released its reservation, so its result is a failure (not a phantom success).
        Assert.Equal(2, results.Count);
        Assert.True(results[0].PlacedSuccessfully, "chunk-1 order should have committed");
        Assert.False(results[1].PlacedSuccessfully, "crashed chunk-2 order must not report success");

        // Reconcile the DURABLE store ALONE (what a restart would cold-load): every surviving Fund is
        // internally valid, no money was created/destroyed (no fills ⇒ totals unchanged), and the
        // crashed chunk's user holds NO durable reservation (its recovery released it on a fresh tx).
        foreach (var kv in w.Db.Funds)
        {
            var (total, reserved) = kv.Value;
            Assert.True(total == StartBalance, $"durable total moved for {kv.Key}");
            Assert.True(reserved >= 0m && reserved <= total, $"durable Fund invariant broken for {kv.Key}");
        }
        if (w.Db.Funds.TryGetValue((2, CurrencyType.USD), out var crashedFund))
            Assert.Equal(0m, crashedFund.Reserved);
        Assert.Empty(w.Db.Transactions); // no fills in this scenario

        // Cache reconciliation: the crashed chunk's user fund reservation was released back to zero, so
        // cache == the recovered durable state for that user (no phantom reservation leak).
        var cacheFund2 = w.Accounts.GetFund(2, CurrencyType.USD);
        Assert.NotNull(cacheFund2);
        Assert.Equal(0m, cacheFund2!.ReservedBalance);
    }

    [Fact]
    public async Task Crash_on_the_only_chunk_commit_recovers_all_and_leaves_nothing_durable_from_it()
    {
        // One USD shard, one chunk (MaxBatch large) holding both groups. Root commits: #1 Phase-2,
        // #2 the chunk ← crash. The entire chunk rolls back; both orders are recovered.
        var w = NewWorld(maxBatch: 64, crashOnRootCommit: 2);

        var orders = new[] { Buy(userId: 1, stockId: 10), Buy(userId: 2, stockId: 11) };
        var results = await w.Engine.PlaceAndMatchBatchAsync(orders);

        Assert.All(results, r => Assert.False(r.PlacedSuccessfully));

        // Durable store stays conserved; both users' reservations were released by recovery.
        foreach (var kv in w.Db.Funds)
        {
            var (total, reserved) = kv.Value;
            Assert.Equal(StartBalance, total);
            Assert.True(reserved >= 0m && reserved <= total);
        }
        foreach (var uid in new[] { 1, 2 })
        {
            var f = w.Accounts.GetFund(uid, CurrencyType.USD);
            Assert.NotNull(f);
            Assert.Equal(0m, f!.ReservedBalance);
        }
        Assert.Empty(w.Db.Transactions);
    }

    /// <summary>Silent ledger — these tests assert on durable rows + cache, not ledger tuples.</summary>
    private sealed class NullReservationLedger : IReservationLedger
    {
        public HashSet<int> TrackedUserIds { get; } = new();
        public bool TrackAll { get; set; }
        public IReadOnlyList<LedgerEntry> Snapshot() => Array.Empty<LedgerEntry>();
        public int EntryCount => 0;
        public string SuggestedExportFileName => "test";
        public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
            decimal amount, decimal reservedBefore, decimal reservedAfter,
            decimal totalBefore, decimal totalAfter) { }
        public void LogPosition(int userId, int stockId, int? orderId, string action,
            decimal amount, int reservedBefore, int reservedAfter,
            int quantityBefore, int quantityAfter) { }
        public void LogOrder(int userId, int orderId, string action, decimal amount,
            decimal buyReservationBefore, decimal buyReservationAfter,
            int sellReservedBefore, int sellReservedAfter) { }
        public void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
            int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount) { }
        public Task<string> ExportCsvAsync(string path, CancellationToken ct = default) => Task.FromResult(path);
        public string BuildCsv(CancellationToken ct = default) => string.Empty;
        public void Clear() { }
    }
}
