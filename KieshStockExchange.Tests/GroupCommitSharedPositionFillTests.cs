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
/// §C FILL-level upgrade of <see cref="GroupCommitSharedPositionEquivalenceTests"/> (its no-fill matcher left
/// the position oracle empty). One user (1) holds ONE currency-agnostic Position (1,10) that BOTH the USD and
/// the EUR book of cross-listed stock 10 mutate; each taker buy fills against a resting maker (seller 2, whose
/// single Position (2,10) both books also drain). Under group-commit each currency commits as a SEPARATE root
/// tx (shard), so the open CK question is whether share-conservation survives on that shared Position — including
/// when one shard commits and the other rolls back.
///
/// Two contracts:
///   1. Equivalence WITH fills — group-commit OFF vs ON leave byte-identical durable Position/Fund/Order rows
///      and identical position-ledger tuples ⇒ share conservation is group-commit-invariant on the shared row.
///   2. Adversarial partial failure — group-commit ON, the EUR shard's root commit dies (fsync death). The USD
///      fill is durable, the EUR fill rolled back; asserting on the DURABLE store ALONE: shares the buyer
///      durably gained == shares sellers durably lost across BOTH currencies, the shared Position (1,10)
///      reflects only the committed shard, the crashed shard's order is recovered (no phantom success), and the
///      cache reservation is released back to zero.
///
/// The durability fake mirrors <see cref="GroupCommitCrashTests"/> (AsyncLocal ambient root tx + nested
/// savepoint frames, durable ONLY on a root commit). The crash is targeted by TRANSACTION CURRENCY rather than
/// a global root-commit index: shards run in parallel (Task.WhenAll) so the index is racy, but only the EUR
/// shard's root frame ever carries an EUR fill (Phase-2 order insert carries none), so the target is exact and
/// order-independent.
/// </summary>
public class GroupCommitSharedPositionFillTests
{
    private const decimal StartBalance = 1_000_000m;
    private const int Buyer = 1;
    private const int Seller = 2;
    private const int StockId = 10;
    private const int Qty = 10;
    private const decimal Price = 100m;
    private const decimal Notional = Qty * Price;
    private const int SellerStartQty = 100;

    // ── Durability fake: writes are durable only after a ROOT commit. Mirrors PgDBService's AsyncLocal ambient
    //    (root tx) + nested SAVEPOINT semantics; adds a currency-targeted crash for the shard-rollback scenario.
    private sealed class FakeDb
    {
        internal sealed class Frame
        {
            public List<Action> Pending = new();
            public Frame? Parent;
            public bool IsRoot;
            public HashSet<CurrencyType> TxCurrencies = new();   // fill currencies staged in this frame
        }
        private readonly AsyncLocal<Frame?> _ambient = new();

        // Durable (committed) snapshots — the "DB alone" view a crash recovery would cold-load from.
        public readonly Dictionary<int, string> Orders = new();
        public readonly Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)> Funds = new();
        public readonly Dictionary<(int, int), (int Qty, int Reserved)> Positions = new();
        public readonly List<Transaction> Transactions = new();

        public int RootCommits;
        public CurrencyType? CrashOnTxCurrency;   // the shard whose root commit dies (fsync death)
        private bool _crashed;
        public int NextId = 1;

        // Cache cold-load sources — funds (both ccy per user) + starting positions.
        public readonly Dictionary<(int, CurrencyType), Fund> SeedFunds = new();
        public readonly Dictionary<(int, int), Position> SeedPositions = new();

        public ITransaction Begin()
        {
            var f = new Frame { Parent = _ambient.Value, IsRoot = _ambient.Value is null };
            _ambient.Value = f;
            return new FrameTx(this, f);
        }

        private void Stage(Action apply)
        {
            var f = _ambient.Value;
            if (f is null) { apply(); return; }
            f.Pending.Add(apply);
        }

        public void StageOrder(Order o) { int id = o.OrderId; string st = o.Status; Stage(() => Orders[id] = st); }
        public void StageFund(Fund x) { var k = (x.UserId, x.CurrencyType); var v = (x.TotalBalance, x.ReservedBalance); Stage(() => Funds[k] = v); }
        public void StagePosition(Position p) { var k = (p.UserId, p.StockId); var v = (p.Quantity, p.ReservedQuantity); Stage(() => Positions[k] = v); }
        public void StageTransaction(Transaction t)
        {
            _ambient.Value?.TxCurrencies.Add(t.CurrencyType);   // tag the frame so the shard commit is targetable
            Stage(() => Transactions.Add(t));
        }

        internal void Commit(Frame f)
        {
            if (f.IsRoot)
            {
                if (!_crashed && CrashOnTxCurrency is { } ccy && f.TxCurrencies.Contains(ccy))
                {
                    _crashed = true;
                    _ambient.Value = f.Parent;                 // clear ambient like a dropped connection
                    throw new InvalidOperationException($"simulated {ccy} shard crash before fsync");
                }
                foreach (var a in f.Pending) a();              // the durable flush (one fsync)
                RootCommits++;
            }
            else
            {
                f.Parent!.Pending.AddRange(f.Pending);         // RELEASE SAVEPOINT: merge into parent
                foreach (var c in f.TxCurrencies) f.Parent.TxCurrencies.Add(c);
            }
            _ambient.Value = f.Parent;
        }

        internal void Rollback(Frame f) => _ambient.Value = f.Parent;

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

    // §C share-conservation oracle — the position-ledger tuples the no-fill fixture left empty.
    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(string Action, decimal Amount, int QtyBefore, int QtyAfter)> PositionEntries = new();
        public readonly List<(string Action, decimal Amount, decimal Before, decimal After)> FundEntries = new();
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
        public AccountsCache Accounts = null!;
        public RecordingLedger Ledger = null!;
        public FakeDb Db = null!;
    }

    // Resting maker sell orders (one per book) the mocked matcher fills the takers against.
    private static Order Maker(int orderId, CurrencyType ccy) => new()
    {
        OrderId = orderId, UserId = Seller, StockId = StockId, CurrencyType = ccy,
        Quantity = Qty, Price = Price, Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
    };

    private static World NewWorld(bool groupCommit, CurrencyType? crashOnTxCurrency)
    {
        var w = new World();
        var fake = new FakeDb { CrashOnTxCurrency = crashOnTxCurrency };
        w.Db = fake;

        // Seed both currencies for buyer + seller, and the two shared starting positions.
        foreach (var uid in new[] { Buyer, Seller })
            foreach (var ccy in new[] { CurrencyType.USD, CurrencyType.EUR })
                fake.SeedFunds[(uid, ccy)] = new Fund { UserId = uid, CurrencyType = ccy, TotalBalance = StartBalance, ReservedBalance = 0m };
        fake.SeedPositions[(Buyer, StockId)] = new Position { PositionId = 501, UserId = Buyer, StockId = StockId, Quantity = 0, ReservedQuantity = 0 };
        fake.SeedPositions[(Seller, StockId)] = new Position { PositionId = 502, UserId = Seller, StockId = StockId, Quantity = SellerStartQty, ReservedQuantity = 0 };
        // Pre-existing durable rows the crash recovery would cold-load.
        foreach (var kv in fake.SeedPositions) fake.Positions[kv.Key] = (kv.Value.Quantity, kv.Value.ReservedQuantity);
        foreach (var kv in fake.SeedFunds) fake.Funds[kv.Key] = (kv.Value.TotalBalance, kv.Value.ReservedBalance);

        var makers = new Dictionary<CurrencyType, Order>
        {
            [CurrencyType.USD] = Maker(9001, CurrencyType.USD),
            [CurrencyType.EUR] = Maker(9002, CurrencyType.EUR),
        };

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) =>
              ids.SelectMany(id => new[] { fake.SeedFunds[(id, CurrencyType.USD)], fake.SeedFunds[(id, CurrencyType.EUR)] }).ToList());
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) =>
              ids.Where(id => fake.SeedPositions.ContainsKey((id, StockId)))
                 .Select(id => fake.SeedPositions[(id, StockId)]).ToList());
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<Order>());

        db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
          .Returns((CancellationToken _) => Task.FromResult(fake.Begin()));
        db.Setup(d => d.RunInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
          .Returns<Func<CancellationToken, Task>, CancellationToken>(async (action, ct) =>
          {
              var tx = fake.Begin();
              try { await action(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
              catch { await tx.RollbackAsync(ct).ConfigureAwait(false); throw; }
          });

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

        var books = new Mock<IOrderBookEngine>();
        books.Setup(b => b.WithBookLockAsync(It.IsAny<int>(), It.IsAny<CurrencyType>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<OrderBook, Task>>()))
             .Returns<int, CurrencyType, CancellationToken, Func<OrderBook, Task>>(
                 (sid, ccy, _, body) => body(new OrderBook(sid, ccy)));

        // Fills-producing matcher: each taker buy fully fills against its currency's resting maker (seller 2),
        // moving Qty shares seller→buyer. taker.Fill mirrors the real MatchingEngine so the taker ends Filled
        // and doesn't rest; the maker rides along in the MakerSnapshot so the engine gates the seller + hands it
        // to the settler (whose long-sell top-up sources the shares from the seller's available quantity).
        var matching = new Mock<IMatchingEngine>();
        matching.Setup(m => m.Match(It.IsAny<Order>(), It.IsAny<OrderBook>(),
                It.IsAny<CancellationToken>(), It.IsAny<TradeBatchScope?>()))
            .Returns<Order, OrderBook, CancellationToken, TradeBatchScope?>((taker, _, __, scope) =>
            {
                if (!taker.IsBuyOrder || taker.RemainingQuantity <= 0)
                    return new MatchResult(new List<Transaction>(), taker.AmountFilled, new List<MakerSnapshot>());
                var maker = makers[taker.CurrencyType];
                scope?.OrderStatusSnapshots.TryAdd(taker.OrderId, taker.Status);
                var fill = new Transaction
                {
                    StockId = StockId, CurrencyType = taker.CurrencyType, Quantity = Qty, Price = Price,
                    BuyerId = Buyer, SellerId = Seller, BuyOrderId = taker.OrderId, SellOrderId = maker.OrderId,
                };
                var original = taker.AmountFilled;
                taker.Fill(Qty);
                return new MatchResult(new List<Transaction> { fill }, original,
                    new List<MakerSnapshot> { new(maker, 0, true) });
            });

        var config = new Mock<IConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns(Mock.Of<IConfigurationSection>());
        void SetKey(string key, string val)
        {
            var sec = new Mock<IConfigurationSection>(); sec.Setup(s => s.Value).Returns(val);
            config.Setup(c => c.GetSection(key)).Returns(sec.Object);
        }
        if (groupCommit)
        {
            SetKey("Db:GroupCommit:Enabled", "true");
            SetKey("Db:GroupCommit:MaxBatch", "64");
        }

        w.Engine = new OrderExecutionService(
            db.Object, books.Object, matching.Object, validator, settlement,
            new Mock<IMarketDataService>().Object, w.Accounts, new Mock<IOrderCacheService>().Object,
            w.Ledger, registry, new Mock<IServerNotificationService>().Object,
            new Mock<IBracketCoordinator>().Object, config.Object,
            NullLogger<OrderExecutionService>.Instance);
        return w;
    }

    private static Order Buy(CurrencyType ccy) => new()
    {
        UserId = Buyer, StockId = StockId, Quantity = Qty, Price = Price,
        CurrencyType = ccy, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
    };

    // One user buys the SAME cross-listed stock on BOTH books ⇒ one shared Position (1,10), one per-currency shard.
    private static IReadOnlyList<Order> CrossListedBuyBatch() => new[] { Buy(CurrencyType.USD), Buy(CurrencyType.EUR) };

    private static List<(string, decimal, int, int)> PositionTuples(RecordingLedger l)
        => l.PositionEntries.Select(e => (e.Action, e.Amount, e.QtyBefore, e.QtyAfter))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ThenBy(t => t.Item4).ToList();

    private static List<(string, decimal, decimal, decimal)> FundTuples(RecordingLedger l)
        => l.FundEntries.Select(e => (e.Action, e.Amount, e.Before, e.After))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ThenBy(t => t.Item4).ToList();

    [Fact]
    public async Task GroupCommit_off_vs_on_withFills_conservesSharedPosition_identically()
    {
        var off = NewWorld(groupCommit: false, crashOnTxCurrency: null);
        var on = NewWorld(groupCommit: true, crashOnTxCurrency: null);

        var offResults = await off.Engine.PlaceAndMatchBatchAsync(CrossListedBuyBatch());
        var onResults = await on.Engine.PlaceAndMatchBatchAsync(CrossListedBuyBatch());

        Assert.All(offResults, r => Assert.True(r.PlacedSuccessfully));
        Assert.All(onResults, r => Assert.True(r.PlacedSuccessfully));

        // Both books settled onto the shared row: buyer +Qty per book, seller −Qty per book. Total per stock
        // unchanged (2×Qty moved, none created/destroyed) and byte-identical OFF vs ON.
        Assert.Equal((2 * Qty, 0), on.Db.Positions[(Buyer, StockId)]);
        Assert.Equal((SellerStartQty - 2 * Qty, 0), on.Db.Positions[(Seller, StockId)]);
        Assert.Equal(off.Db.Positions[(Buyer, StockId)], on.Db.Positions[(Buyer, StockId)]);
        Assert.Equal(off.Db.Positions[(Seller, StockId)], on.Db.Positions[(Seller, StockId)]);

        // Durable Funds identical OFF vs ON (seller credited both notionals; buyer debited both).
        Assert.Equal(off.Db.Funds, on.Db.Funds);
        Assert.Equal(StartBalance + 2 * Notional, on.Db.Funds[(Seller, CurrencyType.USD)].Total
            + on.Db.Funds[(Seller, CurrencyType.EUR)].Total - StartBalance);

        // Durable order rows identical (both takers Filled).
        Assert.Equal(off.Db.Orders, on.Db.Orders);

        // Share-conservation oracle: the position-ledger tuples the no-fill fixture left empty now match OFF vs ON.
        Assert.NotEmpty(on.Ledger.PositionEntries);
        Assert.Equal(PositionTuples(off.Ledger), PositionTuples(on.Ledger));
        Assert.Equal(FundTuples(off.Ledger), FundTuples(on.Ledger));
    }

    [Fact]
    public async Task GroupCommit_on_EURshardCrash_conservesSharedPosition_onDurableStoreAlone()
    {
        // Group-commit ON; the EUR shard's root commit dies. USD's fill is durable, EUR's rolled back.
        var w = NewWorld(groupCommit: true, crashOnTxCurrency: CurrencyType.EUR);

        var results = await w.Engine.PlaceAndMatchBatchAsync(CrossListedBuyBatch());

        // USD committed; the crashed EUR taker must be recovered (not a phantom success).
        Assert.Equal(2, results.Count);
        Assert.True(results[0].PlacedSuccessfully, "USD shard should have committed");
        Assert.False(results[1].PlacedSuccessfully, "crashed EUR taker must not report success");

        // ── Assert on the DURABLE store ALONE (what a restart would cold-load).
        var buyerPos = w.Db.Positions[(Buyer, StockId)];
        var sellerPos = w.Db.Positions[(Seller, StockId)];

        // The shared Position reflects ONLY the committed USD shard — the EUR mutation left nothing durable.
        Assert.Equal((Qty, 0), buyerPos);
        Assert.Equal((SellerStartQty - Qty, 0), sellerPos);

        // Share conservation across BOTH currencies on the durable store: shares the buyer durably gained ==
        // shares the seller durably lost. The EUR leg conserves trivially (0 == 0) because it rolled back whole.
        int buyerGained = buyerPos.Qty - 0;                     // seed buyer Quantity was 0
        int sellerLost = SellerStartQty - sellerPos.Qty;
        Assert.Equal(sellerLost, buyerGained);
        Assert.Equal(Qty, buyerGained);
        Assert.Single(w.Db.Transactions);                        // only the USD fill is durable
        Assert.Equal(CurrencyType.USD, w.Db.Transactions[0].CurrencyType);

        // The USD money leg is durably conserved (seller credited exactly the notional the buyer spent).
        Assert.Equal(StartBalance - Notional, w.Db.Funds[(Buyer, CurrencyType.USD)].Total);
        Assert.Equal(StartBalance + Notional, w.Db.Funds[(Seller, CurrencyType.USD)].Total);
        // The EUR shard rolled back whole: no money moved, and the buyer's EUR reservation was released durably.
        Assert.Equal(StartBalance, w.Db.Funds[(Buyer, CurrencyType.EUR)].Total);
        Assert.Equal(0m, w.Db.Funds[(Buyer, CurrencyType.EUR)].Reserved);
        Assert.Equal(StartBalance, w.Db.Funds[(Seller, CurrencyType.EUR)].Total);

        // Cache reconciliation: the recovered EUR reservation is back to zero (no phantom reservation leak),
        // so the cache Fund matches the recovered durable state.
        var cacheEur = w.Accounts.GetFund(Buyer, CurrencyType.EUR);
        Assert.NotNull(cacheEur);
        Assert.Equal(0m, cacheEur!.ReservedBalance);

        // Cache Position agrees with the durable single-shard state (EUR settle-pass mutation was restored).
        var cachePos = w.Accounts.GetPosition(Buyer, StockId);
        Assert.NotNull(cachePos);
        Assert.Equal(Qty, cachePos!.Quantity);
    }
}
