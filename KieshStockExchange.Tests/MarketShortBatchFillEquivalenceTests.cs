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
/// Slice 2 FILL-PRODUCING equivalence tests for
/// <see cref="OrderExecutionService.PlaceMarketShortBatchAsync"/>.
///
/// The sibling <c>MarketShortBatchEquivalenceTests</c> mocks the matcher to ZERO fills, so it proves
/// placement/rows/ledger but never moves fill-time collateral. This fixture drives a REAL
/// <see cref="MatchingEngine"/> against a PERSISTENT <see cref="OrderBook"/> per (stockId,currency)
/// pre-seeded with resting BUY makers, so a flat market short actually fills and
/// <see cref="TradeSettler"/>'s <c>isShortFill</c> branch reserves cash collateral at fill. Two
/// independent worlds — per-order (<see cref="OrderExecutionService.PlaceAndMatchAsync"/>, looped) vs
/// batched (<see cref="OrderExecutionService.PlaceMarketShortBatchAsync"/>) — are compared on
/// fill-level end state (<c>Fund.TotalBalance</c>/<c>ReservedBalance</c>, <c>Position.Quantity</c>/
/// <c>ShortCollateral</c>), the <see cref="OrderResult"/> per order, persisted rows, and the reservation
/// ledger multiset.
///
/// The KEY fixture is F1 (<see cref="F1_same_user_buyer_and_seller_crossing_short_ordering"/>): a cohort
/// short-seller X who ALSO owns a resting BUY that another cohort short (the "crossing short" Y) fills.
/// X then appears as BOTH a buyer and a seller inside ONE (stockId,currency) group. Because
/// <c>isShortFill</c> classifies off a stale pre-batch snapshot and — unlike <c>isFlipFill</c> — has NO
/// post-<c>ApplyDelta</c> live-<c>Quantity>=0</c> guard, the group's outcome depends on the intra-group
/// fill order (which is submission order == ascending AiUserId): if X's own short-open settles first the
/// later buy is a clean buy-to-close (commits); if Y's crossing buy-fill lands first X's live Quantity is
/// lifted positive yet the short still collateralises, the pre-write CK scan (<c>FindInvariantViolation</c>)
/// trips, and the WHOLE short group rolls back — a batched-vs-per-order divergence (never a money leak).
/// The per-order path settles each taker in isolation so it never sees the cross and always succeeds.
/// </summary>
public class MarketShortBatchFillEquivalenceTests
{
    private const int StockA = 10;
    private const int StockB = 11;
    private const int MakerOwner = 100;   // neutral resting-buy owner (never a cohort short)
    private const CurrencyType Usd = CurrencyType.USD;

    // ---- Row snapshot oracle ---------------------------------------------------------------------
    private sealed record RowSnap(string Status, OrderSide Side, EntryType Entry, StopKind Stop,
        int StockId, int Quantity, decimal Price);

    private static RowSnap Snap(Order o) => new(o.Status, o.Side, o.Entry, o.Stop,
        o.StockId, o.Quantity, o.Price);

    // ---- Recording ledger (records money + share + trade movements) ------------------------------
    private sealed class RecordingLedger : IReservationLedger
    {
        public readonly List<(decimal Amount, decimal ResBefore, decimal ResAfter, decimal TotBefore, decimal TotAfter)> FundEntries = new();
        public readonly List<(decimal Amount, int ResBefore, int ResAfter, int QtyBefore, int QtyAfter)> PositionEntries = new();
        public readonly List<(int Buyer, int Seller, int Qty, decimal Price)> TransactionEntries = new();

        public HashSet<int> TrackedUserIds { get; } = new();
        public bool TrackAll { get; set; }
        public IReadOnlyList<LedgerEntry> Snapshot() => Array.Empty<LedgerEntry>();
        public int EntryCount => 0;
        public string SuggestedExportFileName => "test";
        public void LogFund(int userId, CurrencyType ccy, int? orderId, string action,
            decimal amount, decimal reservedBefore, decimal reservedAfter,
            decimal totalBefore, decimal totalAfter)
            => FundEntries.Add((amount, reservedBefore, reservedAfter, totalBefore, totalAfter));
        public void LogPosition(int userId, int stockId, int? orderId, string action,
            decimal amount, int reservedBefore, int reservedAfter,
            int quantityBefore, int quantityAfter)
            => PositionEntries.Add((amount, reservedBefore, reservedAfter, quantityBefore, quantityAfter));
        public void LogOrder(int userId, int orderId, string action, decimal amount,
            decimal buyReservationBefore, decimal buyReservationAfter,
            int sellReservedBefore, int sellReservedAfter) { }
        public void LogTransaction(int buyerId, int sellerId, int stockId, CurrencyType ccy,
            int buyOrderId, int sellOrderId, int quantity, decimal price, decimal totalAmount)
            => TransactionEntries.Add((buyerId, sellerId, quantity, price));
        public Task<string> ExportCsvAsync(string path, CancellationToken ct = default) => Task.FromResult(path);
        public string BuildCsv(CancellationToken ct = default) => string.Empty;
        public void Clear() { }
    }

    // ---- Matcher decorator: real fills + records taker submission order ---------------------------
    private sealed class RecordingMatchingEngine : IMatchingEngine
    {
        private readonly MatchingEngine _inner = new(NullLogger<MatchingEngine>.Instance);
        public readonly List<int> MatchedTakerIds = new();
        public MatchResult Match(Order taker, OrderBook book, CancellationToken ct, TradeBatchScope? scope = null)
        {
            MatchedTakerIds.Add(taker.OrderId);
            return _inner.Match(taker, book, ct, scope);
        }
    }

    // ---- World: one isolated engine stack with a persistent per-(stock,ccy) book -----------------
    private sealed class World
    {
        public OrderExecutionService Engine = null!;
        public AccountsCache Accounts = null!;
        public OrderRegistry Registry = null!;
        public RecordingLedger Ledger = null!;
        public RecordingMatchingEngine Matcher = null!;
        public readonly List<RowSnap> InsertedRows = new();

        // Seeded backing store the DB mock reads from.
        public readonly Dictionary<(int UserId, CurrencyType Ccy), Fund> Funds = new();
        public readonly Dictionary<(int UserId, int StockId), Position> Positions = new();
        public readonly Dictionary<int, List<Order>> OpenOrdersByUser = new();
        public readonly Dictionary<(int, CurrencyType), OrderBook> Books = new();
        private int _nextMakerId = 10_001;

        public OrderBook BookFor(int stockId, CurrencyType ccy)
        {
            if (!Books.TryGetValue((stockId, ccy), out var b))
                Books[(stockId, ccy)] = b = new OrderBook(stockId, ccy);
            return b;
        }

        public Fund SeedFund(int userId, decimal total = 1_000_000m, CurrencyType ccy = Usd)
        {
            var f = new Fund { UserId = userId, CurrencyType = ccy, TotalBalance = total, ReservedBalance = 0m };
            Funds[(userId, ccy)] = f;
            return f;
        }

        // A pre-existing flat/long position row (PositionId set ⇒ loaded, not brand-new).
        public Position SeedPosition(int userId, int stockId, int qty, int positionId)
        {
            var p = new Position { PositionId = positionId, UserId = userId, StockId = stockId, Quantity = qty };
            Positions[(userId, stockId)] = p;
            return p;
        }

        // A resting BUY-limit maker: registered + on the book + returned by GetOpenOrdersForUsers so
        // EnsureLoadedAsync re-seeds its buy reservation on the SAME instance the book/settler use.
        public Order SeedBuyMaker(int userId, int qty, decimal price, int stockId = StockA, CurrencyType ccy = Usd)
        {
            var maker = new Order
            {
                OrderId = _nextMakerId++, UserId = userId, StockId = stockId, Quantity = qty, Price = price,
                CurrencyType = ccy, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
            };
            Registry.Register(maker);
            BookFor(stockId, ccy).UpsertOrder(maker);
            if (!OpenOrdersByUser.TryGetValue(userId, out var list))
                OpenOrdersByUser[userId] = list = new List<Order>();
            list.Add(maker);
            if (!Funds.ContainsKey((userId, ccy))) SeedFund(userId, ccy: ccy);
            return maker;
        }

        // A resting SELL-limit maker (mirror of SeedBuyMaker) — lets a taker BUY cross it. The owner must
        // already hold the shares (seed a long via SeedPosition) so the settle is a plain long-sale.
        public Order SeedSellMaker(int userId, int qty, decimal price, int stockId = StockA, CurrencyType ccy = Usd)
        {
            var maker = new Order
            {
                OrderId = _nextMakerId++, UserId = userId, StockId = stockId, Quantity = qty, Price = price,
                CurrencyType = ccy, Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
            };
            Registry.Register(maker);
            BookFor(stockId, ccy).UpsertOrder(maker);
            if (!OpenOrdersByUser.TryGetValue(userId, out var list))
                OpenOrdersByUser[userId] = list = new List<Order>();
            list.Add(maker);
            if (!Funds.ContainsKey((userId, ccy))) SeedFund(userId, ccy: ccy);
            return maker;
        }

        // Load every seeded fund-owner into the cache BEFORE any fill, exactly like a live session
        // hydrates all bot users at startup — so a maker's buy reservation is seeded off its FULL
        // unfilled RemainingQuantity, and the settler's later EnsureLoadedAsync is a no-op.
        public Task PreloadAsync()
        {
            var users = new HashSet<int>();
            foreach (var k in Funds.Keys) users.Add(k.UserId);
            foreach (var k in Positions.Keys) users.Add(k.UserId);
            foreach (var k in OpenOrdersByUser.Keys) users.Add(k);
            return Accounts.EnsureLoadedAsync(users.ToList(), default);
        }
    }

    private static World NewWorld(bool groupCommit = false)
    {
        var w = new World();
        var registry = new OrderRegistry();
        w.Registry = registry;

        var db = new Mock<IDataBaseService>(MockBehavior.Loose);
        db.Setup(d => d.GetFundsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) =>
              w.Funds.Where(kv => ids.Contains(kv.Key.UserId)).Select(kv => kv.Value).ToList());
        db.Setup(d => d.GetPositionsForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) =>
              w.Positions.Where(kv => ids.Contains(kv.Key.UserId)).Select(kv => kv.Value).ToList());
        db.Setup(d => d.GetOpenOrdersForUsersAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((List<int> ids, CancellationToken _) =>
              ids.SelectMany(id => w.OpenOrdersByUser.TryGetValue(id, out var l) ? l : Enumerable.Empty<Order>()).ToList());

        int nextId = 1;
        var byId = new Dictionary<int, Order>();
        db.Setup(d => d.CreateOrder(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
          .Callback<Order, CancellationToken>((o, _) => { o.OrderId = nextId++; byId[o.OrderId] = o; w.InsertedRows.Add(Snap(o)); })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.InsertAllAsync(It.IsAny<IEnumerable<Order>>(), It.IsAny<CancellationToken>()))
          .Callback<IEnumerable<Order>, CancellationToken>((items, _) =>
          { foreach (var o in items) { o.OrderId = nextId++; byId[o.OrderId] = o; w.InsertedRows.Add(Snap(o)); } })
          .Returns(Task.CompletedTask);
        db.Setup(d => d.GetOrderById(It.IsAny<int>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync((int id, CancellationToken _) => byId.TryGetValue(id, out var o) ? o : null);
        db.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
          .ReturnsAsync(new Mock<ITransaction>().Object);
        db.Setup(d => d.RunInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
          .Returns<Func<CancellationToken, Task>, CancellationToken>((action, ct) => action(ct));

        w.Ledger = new RecordingLedger();
        var accounts = new AccountsCache(db.Object, registry, w.Ledger, NullLogger<AccountsCache>.Instance);
        w.Accounts = accounts;

        var stocks = new Mock<IStockService>();
        Stock? stockOut = new Stock();
        stocks.Setup(s => s.TryGetById(It.IsAny<int>(), out stockOut)).Returns(true);
        stocks.Setup(s => s.IsListedIn(It.IsAny<int>(), It.IsAny<CurrencyType>())).Returns(true);
        var validator = new OrderValidator(stocks.Object);

        var settlement = new SettlementEngine(db.Object, accounts, w.Ledger, registry,
            NullLogger<SettlementEngine>.Instance, NullLoggerFactory.Instance,
            Options.Create(new SeparatorLoggerOptions()));

        // Persistent per-(stock,ccy) book: return the SAME instance every call so the resting makers
        // seeded up-front are actually there when the matcher runs.
        var books = new Mock<IOrderBookEngine>();
        books.Setup(b => b.WithBookLockAsync(It.IsAny<int>(), It.IsAny<CurrencyType>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<OrderBook, Task>>()))
             .Returns<int, CurrencyType, CancellationToken, Func<OrderBook, Task>>(
                 (sid, ccy, _, body) => body(w.BookFor(sid, ccy)));

        w.Matcher = new RecordingMatchingEngine();

        var config = new Mock<IConfiguration>();
        config.Setup(c => c.GetSection(It.IsAny<string>())).Returns(Mock.Of<IConfigurationSection>());
        if (groupCommit)
        {
            var onSection = new Mock<IConfigurationSection>();
            onSection.Setup(s => s.Value).Returns("true");
            config.Setup(c => c.GetSection("Db:GroupCommit:Enabled")).Returns(onSection.Object);
        }

        w.Engine = new OrderExecutionService(
            db.Object, books.Object, w.Matcher, validator, settlement,
            new Mock<IMarketDataService>().Object, accounts, new Mock<IOrderCacheService>().Object,
            w.Ledger, registry, new Mock<IServerNotificationService>().Object,
            new Mock<IBracketCoordinator>().Object, config.Object,
            NullLogger<OrderExecutionService>.Instance);
        return w;
    }

    private static Order ShortSell(int userId, int qty, int stockId = StockA, CurrencyType ccy = Usd) => new()
    {
        UserId = userId, StockId = stockId, Quantity = qty, Price = 0m,
        CurrencyType = ccy, Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
    };

    // ---- Oracles ---------------------------------------------------------------------------------
    private static void AssertResultEqual(OrderResult a, OrderResult b)
    {
        Assert.Equal(a.PlacedSuccessfully, b.PlacedSuccessfully);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.TotalFilledQuantity, b.TotalFilledQuantity);
        Assert.Equal(a.AverageFillPrice, b.AverageFillPrice);
        Assert.Equal(a.FillTransactions.Count, b.FillTransactions.Count);
    }

    private static void AssertFundEqual(World a, World b, int userId, CurrencyType ccy = Usd)
    {
        var fa = a.Accounts.GetFund(userId, ccy);
        var fb = b.Accounts.GetFund(userId, ccy);
        Assert.Equal(fa?.TotalBalance, fb?.TotalBalance);
        Assert.Equal(fa?.ReservedBalance, fb?.ReservedBalance);
    }

    private static void AssertPositionEqual(World a, World b, int userId, int stockId)
    {
        var pa = a.Accounts.GetPosition(userId, stockId);
        var pb = b.Accounts.GetPosition(userId, stockId);
        Assert.Equal(pa?.Quantity ?? 0, pb?.Quantity ?? 0);
        Assert.Equal(pa?.ReservedQuantity ?? 0, pb?.ReservedQuantity ?? 0);
        Assert.Equal(pa?.ShortCollateral ?? 0m, pb?.ShortCollateral ?? 0m);
        if ((pa?.ShortCollateral ?? 0m) > 0m || (pb?.ShortCollateral ?? 0m) > 0m)
            Assert.Equal(pa?.ShortCollateralCurrency, pb?.ShortCollateralCurrency);
    }

    private static List<RowSnap> SortedRows(IEnumerable<RowSnap> rows)
        => rows.OrderBy(r => r.StockId).ThenBy(r => (int)r.Side).ThenBy(r => (int)r.Entry)
            .ThenBy(r => r.Quantity).ThenBy(r => r.Price).ThenBy(r => r.Status).ToList();

    private static List<(decimal, decimal, decimal, decimal, decimal)> FundTuples(RecordingLedger l)
        => l.FundEntries.OrderBy(t => t.Amount).ThenBy(t => t.ResBefore).ThenBy(t => t.ResAfter)
            .ThenBy(t => t.TotBefore).ThenBy(t => t.TotAfter).ToList();

    private static List<(decimal, int, int, int, int)> PositionTuples(RecordingLedger l)
        => l.PositionEntries.OrderBy(t => t.Amount).ThenBy(t => t.ResBefore).ThenBy(t => t.ResAfter)
            .ThenBy(t => t.QtyBefore).ThenBy(t => t.QtyAfter).ToList();

    private static List<(int, int, int, decimal)> TxTuples(RecordingLedger l)
        => l.TransactionEntries.OrderBy(t => t.Buyer).ThenBy(t => t.Seller).ThenBy(t => t.Qty).ThenBy(t => t.Price).ToList();

    private static void AssertLedgerEqual(World a, World b)
    {
        Assert.Equal(FundTuples(a.Ledger), FundTuples(b.Ledger));
        Assert.Equal(PositionTuples(a.Ledger), PositionTuples(b.Ledger));
        Assert.Equal(TxTuples(a.Ledger), TxTuples(b.Ledger));
    }

    // Run a list of shorts through the per-order path (World A) in submission order.
    private static async Task<List<OrderResult>> PerOrderAsync(World a, IEnumerable<Order> shorts)
    {
        var results = new List<OrderResult>();
        foreach (var s in shorts) results.Add(await a.Engine.PlaceAndMatchAsync(s));
        return results;
    }

    // =========================================================================================
    // Fixture 1: flat short, full fill.
    // =========================================================================================
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flat_short_full_fill_matches_per_order(bool groupCommit)
    {
        World Seed(bool gc)
        {
            var w = NewWorld(gc);
            w.SeedFund(1);                                  // short seller
            w.SeedBuyMaker(MakerOwner, qty: 10, price: 100m);
            return w;
        }

        var a = Seed(false);
        var b = Seed(groupCommit);
        await a.PreloadAsync();
        await b.PreloadAsync();

        var ra = await PerOrderAsync(a, new[] { ShortSell(1, 10) });
        var rb = await b.Engine.PlaceMarketShortBatchAsync(new[] { ShortSell(1, 10) });

        Assert.Single(rb);
        AssertResultEqual(ra[0], rb[0]);
        Assert.True(rb[0].PlacedSuccessfully);
        Assert.Equal(10, rb[0].TotalFilledQuantity);

        AssertFundEqual(a, b, 1);
        AssertFundEqual(a, b, MakerOwner);
        AssertPositionEqual(a, b, 1, StockA);           // seller short: Q=-10, collateral 1000
        AssertPositionEqual(a, b, MakerOwner, StockA);  // maker long: Q=+10

        var pShort = b.Accounts.GetPosition(1, StockA);
        Assert.Equal(-10, pShort!.Quantity);
        Assert.True(pShort.ShortCollateral > 0m, "flat short must post collateral at fill");

        Assert.Equal(SortedRows(a.InsertedRows), SortedRows(b.InsertedRows));
        AssertLedgerEqual(a, b);
    }

    // =========================================================================================
    // Regression: CROSS-CURRENCY short close must release the collateral in its OWN currency.
    // The FX-desk house shorts collateralized in EUR then covers with a USD fill. The buggy path
    // only handled collateral==fill-ccy and stranded the EUR collateral on a now-flat position,
    // tripping the Q7 DB invariant (a non-negative position may not carry collateral) in a stuck
    // loop. The fix releases the collateral from the EUR fund regardless of the fill currency.
    // =========================================================================================
    [Fact]
    public async Task Cross_currency_short_close_releases_collateral_in_collateral_currency()
    {
        const CurrencyType Eur = CurrencyType.EUR;
        const int Shorter = 1, LongMaker = 2;
        var w = NewWorld();

        // Shorter holds a 10-share short on StockA collateralized in EUR (1000 EUR reserved) — the
        // state the FX house is in after an EUR short open. Its USD fund funds the buy-to-close.
        var shortPos = w.SeedPosition(Shorter, StockA, qty: -10, positionId: 5001);
        shortPos.TakeShortCollateral(1000m, Eur);
        var eurFund = w.SeedFund(Shorter, total: 1_000_000m, ccy: Eur);
        eurFund.ReserveFunds(1000m);                       // the collateral lock
        w.SeedFund(Shorter, total: 1_000_000m, ccy: Usd);  // cash to buy-to-close in USD

        // A USD sell maker (owned by a long holder) for the close-buy to cross.
        w.SeedPosition(LongMaker, StockA, qty: 10, positionId: 5002);
        w.SeedSellMaker(LongMaker, qty: 10, price: 100m, stockId: StockA, ccy: Usd);

        await w.PreloadAsync();

        // Shorter buys 10 @ 100 in USD → covers the EUR-collateralized short (cross-currency close).
        var closeBuy = new Order
        {
            UserId = Shorter, StockId = StockA, Quantity = 10, Price = 100m,
            CurrencyType = Usd, Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        var res = await w.Engine.PlaceAndMatchAsync(closeBuy);

        Assert.True(res.PlacedSuccessfully);
        Assert.Equal(10, res.TotalFilledQuantity);

        // Position is flat and — critically — carries NO stranded collateral (the invariant Q7 enforces).
        var pos = w.Accounts.GetPosition(Shorter, StockA)!;
        Assert.Equal(0, pos.Quantity);
        Assert.Equal(0m, pos.ShortCollateral);

        // The 1000 EUR collateral was UNRESERVED from the EUR fund (not the USD fill fund), conserving money.
        var eur = w.Accounts.GetFund(Shorter, Eur)!;
        Assert.Equal(0m, eur.ReservedBalance);
        Assert.Equal(1_000_000m, eur.TotalBalance);        // collateral is a reservation, never a debit
    }

    // =========================================================================================
    // Fixture 2: flat short partially filled, remainder cancelled (market order).
    // =========================================================================================
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flat_short_partial_fill_then_cancel_matches_per_order(bool groupCommit)
    {
        World Seed(bool gc)
        {
            var w = NewWorld(gc);
            w.SeedFund(1);
            w.SeedBuyMaker(MakerOwner, qty: 4, price: 100m);  // only 4 of the 10-share short fill
            return w;
        }

        var a = Seed(false);
        var b = Seed(groupCommit);
        await a.PreloadAsync();
        await b.PreloadAsync();

        var ra = await PerOrderAsync(a, new[] { ShortSell(1, 10) });
        var rb = await b.Engine.PlaceMarketShortBatchAsync(new[] { ShortSell(1, 10) });

        AssertResultEqual(ra[0], rb[0]);
        Assert.Equal(4, rb[0].TotalFilledQuantity);

        AssertFundEqual(a, b, 1);
        AssertFundEqual(a, b, MakerOwner);
        AssertPositionEqual(a, b, 1, StockA);
        AssertPositionEqual(a, b, MakerOwner, StockA);

        Assert.Equal(-4, b.Accounts.GetPosition(1, StockA)!.Quantity);
        Assert.Equal(SortedRows(a.InsertedRows), SortedRows(b.InsertedRows));
        AssertLedgerEqual(a, b);
    }

    // =========================================================================================
    // Fixture 3: two shorts by the SAME seller on the SAME stock (intra-group).
    // =========================================================================================
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_shorts_same_seller_same_stock_matches_per_order(bool groupCommit)
    {
        World Seed(bool gc)
        {
            var w = NewWorld(gc);
            w.SeedFund(1);
            w.SeedBuyMaker(MakerOwner, qty: 100, price: 100m); // deep book fills both shorts
            return w;
        }

        var a = Seed(false);
        var b = Seed(groupCommit);
        await a.PreloadAsync();
        await b.PreloadAsync();

        var shorts = new[] { ShortSell(1, 10), ShortSell(1, 5) };
        var ra = await PerOrderAsync(a, shorts.Select(s => ShortSell(s.UserId, s.Quantity)));
        var rb = await b.Engine.PlaceMarketShortBatchAsync(new[] { ShortSell(1, 10), ShortSell(1, 5) });

        Assert.Equal(2, rb.Count);
        AssertResultEqual(ra[0], rb[0]);
        AssertResultEqual(ra[1], rb[1]);

        AssertFundEqual(a, b, 1);
        AssertPositionEqual(a, b, 1, StockA);
        Assert.Equal(-15, b.Accounts.GetPosition(1, StockA)!.Quantity);
        Assert.Equal(SortedRows(a.InsertedRows), SortedRows(b.InsertedRows));
        AssertLedgerEqual(a, b);
    }

    // =========================================================================================
    // Fixture 4: one seller shorts TWO stocks in the same currency (cross-group same-fund gate).
    // Batched arm runs groupCommit:false ⇒ the two (stock,USD) groups run in parallel and both gate
    // the seller's shared (user,USD) fund; end state is the collateral sum either way.
    // =========================================================================================
    [Fact]
    public async Task Short_two_stocks_same_currency_cross_group_matches_per_order()
    {
        World Seed()
        {
            var w = NewWorld(groupCommit: false);
            w.SeedFund(1);
            w.SeedBuyMaker(MakerOwner, qty: 10, price: 100m, stockId: StockA);
            w.SeedBuyMaker(MakerOwner, qty: 10, price: 100m, stockId: StockB);
            return w;
        }

        var a = Seed();
        var b = Seed();
        await a.PreloadAsync();
        await b.PreloadAsync();

        var ra = await PerOrderAsync(a, new[] { ShortSell(1, 10, StockA), ShortSell(1, 10, StockB) });
        var rb = await b.Engine.PlaceMarketShortBatchAsync(new[] { ShortSell(1, 10, StockA), ShortSell(1, 10, StockB) });

        Assert.Equal(2, rb.Count);
        Assert.All(rb, r => Assert.True(r.PlacedSuccessfully));

        // Same seller's shared USD fund carries both collateral holds.
        AssertFundEqual(a, b, 1);
        AssertPositionEqual(a, b, 1, StockA);
        AssertPositionEqual(a, b, 1, StockB);
        Assert.Equal(-10, b.Accounts.GetPosition(1, StockA)!.Quantity);
        Assert.Equal(-10, b.Accounts.GetPosition(1, StockB)!.Quantity);

        // Fund.ReservedBalance == sum of the two per-stock collateral holds.
        var fA = a.Accounts.GetFund(1, Usd)!;
        Assert.Equal(fA.ReservedBalance,
            (a.Accounts.GetPosition(1, StockA)!.ShortCollateral + a.Accounts.GetPosition(1, StockB)!.ShortCollateral));

        Assert.Equal(SortedRows(a.InsertedRows), SortedRows(b.InsertedRows));
        // Ledger multiset is order-independent (distinct-stock groups + a single shared fund whose
        // per-entry before/after chain the fund gate serialises identically to the sequential path).
        AssertLedgerEqual(a, b);
    }

    // =========================================================================================
    // Fixture 5 / F1: THE KEY DELIVERABLE.
    // A cohort short-seller X ALSO owns a resting BUY that the crossing short Y fills, so X is both a
    // buyer and a seller inside one (StockA,USD) group. Asserted under BOTH orderings of the crossing
    // short relative to X (its AiUserId lower vs higher). See class summary for the mechanism.
    //
    //   • crossingShortLower == false: X (lower id) submitted first ⇒ X's short-open settles before the
    //     crossing buy-fill ⇒ that buy is a clean buy-to-close ⇒ group COMMITS ⇒ batched == per-order.
    //   • crossingShortLower == true:  Y (lower id) submitted first ⇒ X's live Quantity is lifted
    //     positive by the crossing buy-fill BEFORE X's short settles ⇒ isShortFill still collateralises
    //     on a non-negative position ⇒ pre-write CK scan trips ⇒ the WHOLE short group ROLLS BACK.
    //     Per-order settles each taker alone (X's own short flips to a NoopShortPart long-close) and
    //     BOTH shorts succeed. => DIVERGENCE (never a money leak; the group is caught + rolled back).
    // =========================================================================================
    [Theory]
    [InlineData(false)] // crossing short has HIGHER id than X ⇒ commits, equivalent
    [InlineData(true)]  // crossing short has LOWER id than X ⇒ batched group rolls back, per-order succeeds
    public async Task F1_same_user_buyer_and_seller_crossing_short_ordering(bool crossingShortLower)
    {
        // X owns the resting buy that the crossing short fills; Y is the crossing short.
        // Choose ids so the SUBMISSION order (ascending id) realises the intended fill order.
        int x = crossingShortLower ? 2 : 1;   // X's short-open
        int y = crossingShortLower ? 1 : 2;   // Y = crossing short that fills X's resting buy

        World Seed(bool gc)
        {
            var w = NewWorld(gc);
            w.SeedFund(x);
            w.SeedFund(y);
            w.SeedFund(MakerOwner);
            // MX (owned by X) rests at the HIGHER price so the crossing short hits it first; X's own
            // short skips it (self-trade) and fills M1 (neutral) at the lower price.
            w.SeedBuyMaker(x, qty: 10, price: 100m);          // MX — X's resting buy
            w.SeedBuyMaker(MakerOwner, qty: 10, price: 99m);  // M1 — neutral liquidity for X's short
            return w;
        }

        var a = Seed(false);
        var b = Seed(false); // single stock ⇒ one group ⇒ no Task.WhenAll interleaving

        await a.PreloadAsync();
        await b.PreloadAsync();

        // Submit ascending by id (== the cohort's AiUserId order).
        Order[] BatchFor() => (crossingShortLower
            ? new[] { ShortSell(y, 10), ShortSell(x, 10) }   // Y(1) then X(2)
            : new[] { ShortSell(x, 10), ShortSell(y, 10) }); // X(1) then Y(2)

        var ra = await PerOrderAsync(a, BatchFor());
        var rb = await b.Engine.PlaceMarketShortBatchAsync(BatchFor());

        Assert.Equal(2, ra.Count);
        Assert.Equal(2, rb.Count);   // guard against a vacuous Assert.All on an empty result set

        // Per-order ALWAYS succeeds in both orderings (each taker settled in isolation).
        Assert.All(ra, r => Assert.True(r.PlacedSuccessfully,
            $"per-order short should always succeed; got {r.Status}: {r.ErrorMessage}"));

        if (!crossingShortLower)
        {
            // Ordering that COMMITS: batched == per-order end-to-end.
            Assert.All(rb, r => Assert.True(r.PlacedSuccessfully));
            AssertFundEqual(a, b, x);
            AssertFundEqual(a, b, y);
            AssertFundEqual(a, b, MakerOwner);
            AssertPositionEqual(a, b, x, StockA);
            AssertPositionEqual(a, b, y, StockA);
            AssertPositionEqual(a, b, MakerOwner, StockA);
            // X bought back into its own short ⇒ flat, no collateral.
            var px = b.Accounts.GetPosition(x, StockA);
            Assert.Equal(0, px?.Quantity ?? 0);
            Assert.Equal(0m, px?.ShortCollateral ?? 0m);
        }
        else
        {
            // DIVERGENCE: the whole short GROUP rolled back under the batched path.
            Assert.All(rb, r => Assert.False(r.PlacedSuccessfully,
                "batched short group must roll back when the crossing buy-fill lifts the seller " +
                "positive before its short settles (isShortFill has no live-Quantity guard)"));
            // The rollback is specifically the pre-write CK (Q7) invariant scan tripping, not some
            // unrelated settle error — the faithful signature of the missing live-Quantity guard.
            Assert.All(rb, r => Assert.Contains("CK", r.ErrorMessage, StringComparison.OrdinalIgnoreCase));
        }
    }

    // =========================================================================================
    // Fixture 6: zero-liquidity flat short — no fills, remainder cancelled. Collateral trivially 0==0,
    // so the ledger/rows/Status oracle carries the equivalence (no position is created).
    // =========================================================================================
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Zero_liquidity_short_no_fill_matches_per_order(bool groupCommit)
    {
        World Seed(bool gc)
        {
            var w = NewWorld(gc);
            w.SeedFund(1);       // empty book — nothing to fill against
            return w;
        }

        var a = Seed(false);
        var b = Seed(groupCommit);
        await a.PreloadAsync();
        await b.PreloadAsync();

        var ra = await PerOrderAsync(a, new[] { ShortSell(1, 10) });
        var rb = await b.Engine.PlaceMarketShortBatchAsync(new[] { ShortSell(1, 10) });

        AssertResultEqual(ra[0], rb[0]);
        Assert.Equal(0, rb[0].TotalFilledQuantity);

        // No fill ⇒ no position row, no collateral, no transaction ledger rows.
        Assert.Null(b.Accounts.GetPosition(1, StockA));
        AssertFundEqual(a, b, 1);
        Assert.Empty(b.Ledger.TransactionEntries);
        Assert.Equal(SortedRows(a.InsertedRows), SortedRows(b.InsertedRows));
        AssertLedgerEqual(a, b);
    }
}
