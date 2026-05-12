using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices.Tests;

/// <summary>
/// Standalone deterministic harness for the rollback path of
/// <see cref="SettlementEngine.SettleTradesAsync"/>. Wires up a real
/// <see cref="AccountsCache"/> + <see cref="SettlementEngine"/> against a fake
/// <see cref="IDataBaseService"/> that lets us force a tx failure on demand,
/// then asserts that the in-memory cache is restored to its pre-mutation state.
///
/// Not part of any test runner — invoke from anywhere with:
/// <code>
/// var report = await SettlementRollbackSelfTest.RunAllAsync();
/// foreach (var line in report.Lines) Console.WriteLine(line);
/// </code>
///
/// Returns a <see cref="SelfTestReport"/> describing each scenario's outcome.
/// </summary>
public static class SettlementRollbackSelfTest
{
    public sealed record SelfTestReport(int Passed, int Failed, IReadOnlyList<string> Lines)
    {
        public bool AllPassed => Failed == 0;
    }

    public static async Task<SelfTestReport> RunAllAsync(ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var lines = new List<string>();
        int passed = 0, failed = 0;

        async Task RunCase(string name, Func<ILoggerFactory, CancellationToken, Task> body)
        {
            try
            {
                await body(loggerFactory!, ct).ConfigureAwait(false);
                lines.Add($"PASS  {name}");
                passed++;
            }
            catch (Exception ex)
            {
                lines.Add($"FAIL  {name}: {ex.Message}");
                failed++;
            }
        }

        await RunCase(nameof(HappyPath_AppliesDeltasToCache), HappyPath_AppliesDeltasToCache);
        await RunCase(nameof(DbFailure_RestoresFundAndPositionBalances), DbFailure_RestoresFundAndPositionBalances);
        await RunCase(nameof(DbFailure_NewPositionNotLeakedIntoCache), DbFailure_NewPositionNotLeakedIntoCache);
        await RunCase(nameof(DbFailure_RestoresBuyBudget), DbFailure_RestoresBuyBudget);

        lines.Insert(0, $"--- SettlementRollbackSelfTest: {passed}/{passed + failed} passed ---");
        return new SelfTestReport(passed, failed, lines);
    }

    // -----------------------------------------------------------------------
    // Scenarios
    // -----------------------------------------------------------------------

    private static async Task HappyPath_AppliesDeltasToCache(ILoggerFactory factory, CancellationToken ct)
    {
        var (db, cache, engine) = BuildEngine(factory);

        // Buyer userId=1 has $1000 USD, no position. Seller userId=2 has $0, owns 50 shares.
        db.SeedFund(userId: 1, ccy: CurrencyType.USD, balance: 1000m);
        db.SeedFund(userId: 2, ccy: CurrencyType.USD, balance: 0m);
        db.SeedPosition(userId: 2, stockId: 100, qty: 50);

        var orders = MakeOrderMap(
            BuyLimit (orderId: 1001, userId: 1, stockId: 100, qty: 10, price: 5m),
            SellLimit(orderId: 1002, userId: 2, stockId: 100, qty: 10, price: 5m)
        );
        var trades = new List<Transaction>
        {
            MakeTrade(buyOrderId: 1001, sellOrderId: 1002, buyerId: 1, sellerId: 2,
                      stockId: 100, qty: 10, price: 5m),
        };

        var err = await engine.SettleTradesAsync(trades, orders, ct).ConfigureAwait(false);

        AssertNull(err, "SettleTradesAsync should succeed");
        AssertEqual(950m,  cache.GetFund(1, CurrencyType.USD)?.TotalBalance,  "buyer balance after debit");
        AssertEqual(50m,   cache.GetFund(2, CurrencyType.USD)?.TotalBalance,  "seller balance after credit");
        AssertEqual(10,    cache.GetPosition(1, 100)?.Quantity,               "buyer position after credit");
        AssertEqual(40,    cache.GetPosition(2, 100)?.Quantity,               "seller position after debit");
        AssertEqual(1,     db.WrittenTradeCount,                              "DB received 1 trade insert");
    }

    private static async Task DbFailure_RestoresFundAndPositionBalances(ILoggerFactory factory, CancellationToken ct)
    {
        var (db, cache, engine) = BuildEngine(factory);

        db.SeedFund(userId: 1, ccy: CurrencyType.USD, balance: 1000m);
        db.SeedFund(userId: 2, ccy: CurrencyType.USD, balance: 0m);
        db.SeedPosition(userId: 1, stockId: 100, qty: 0);
        db.SeedPosition(userId: 2, stockId: 100, qty: 50);

        var orders = MakeOrderMap(
            BuyLimit (orderId: 2001, userId: 1, stockId: 100, qty: 10, price: 5m),
            SellLimit(orderId: 2002, userId: 2, stockId: 100, qty: 10, price: 5m)
        );
        var trades = new List<Transaction>
        {
            MakeTrade(buyOrderId: 2001, sellOrderId: 2002, buyerId: 1, sellerId: 2,
                      stockId: 100, qty: 10, price: 5m),
        };

        // Capture pre-call state for comparison.
        // Note: AccountsCache.EnsureLoadedAsync is invoked from inside SettleTradesAsync;
        // grab the instances after that point by warming the cache here directly.
        await cache.EnsureLoadedAsync(new[] { 1, 2 }, ct).ConfigureAwait(false);
        var buyerFund = cache.GetFund(1, CurrencyType.USD)!;
        var sellerFund = cache.GetFund(2, CurrencyType.USD)!;
        var buyerPos = cache.GetPosition(1, 100)!;
        var sellerPos = cache.GetPosition(2, 100)!;

        db.FailNextWrite = FailMode.OnUpdateAllFunds;

        var err = await engine.SettleTradesAsync(trades, orders, ct).ConfigureAwait(false);

        AssertNotNull(err,                                 "SettleTradesAsync should report failure");
        AssertEqual(1000m, buyerFund.TotalBalance,         "buyer fund restored");
        AssertEqual(0m,    sellerFund.TotalBalance,        "seller fund restored");
        AssertEqual(0,     buyerPos.Quantity,              "buyer position restored");
        AssertEqual(50,    sellerPos.Quantity,             "seller position restored");
        AssertEqual(0,     db.CommittedFundUpdates,        "no fund update committed to DB");
    }

    private static async Task DbFailure_NewPositionNotLeakedIntoCache(ILoggerFactory factory, CancellationToken ct)
    {
        var (db, cache, engine) = BuildEngine(factory);

        // Buyer has no position row yet — settlement would create one.
        db.SeedFund(userId: 1, ccy: CurrencyType.USD, balance: 1000m);
        db.SeedFund(userId: 2, ccy: CurrencyType.USD, balance: 0m);
        db.SeedPosition(userId: 2, stockId: 100, qty: 50);

        var orders = MakeOrderMap(
            BuyLimit (orderId: 3001, userId: 1, stockId: 100, qty: 10, price: 5m),
            SellLimit(orderId: 3002, userId: 2, stockId: 100, qty: 10, price: 5m)
        );
        var trades = new List<Transaction>
        {
            MakeTrade(buyOrderId: 3001, sellOrderId: 3002, buyerId: 1, sellerId: 2,
                      stockId: 100, qty: 10, price: 5m),
        };

        db.FailNextWrite = FailMode.OnInsertAllPositions;

        var err = await engine.SettleTradesAsync(trades, orders, ct).ConfigureAwait(false);

        AssertNotNull(err,                                  "SettleTradesAsync should report failure");
        AssertNull(cache.GetPosition(1, 100),               "no phantom position in cache after rollback");
    }

    private static async Task DbFailure_RestoresBuyBudget(ILoggerFactory factory, CancellationToken ct)
    {
        var (db, cache, engine) = BuildEngine(factory);

        db.SeedFund(userId: 1, ccy: CurrencyType.USD, balance: 1000m);
        db.SeedFund(userId: 2, ccy: CurrencyType.USD, balance: 0m);
        db.SeedPosition(userId: 2, stockId: 100, qty: 50);

        // TrueMarketBuy: BuyBudget required. After matching, settlement decrements it
        // by the amount actually spent. On failure, it should be restored.
        var truMktBuy = new Order
        {
            UserId = 1, StockId = 100, Quantity = 10, Price = 0m,
            CurrencyType = CurrencyType.USD, OrderType = Order.Types.TrueMarketBuy,
            BuyBudget = 1000m,
        };
        truMktBuy.OrderId = 4001;

        var sell = SellLimit(orderId: 4002, userId: 2, stockId: 100, qty: 10, price: 5m);
        var orders = MakeOrderMap(truMktBuy, sell);

        var trades = new List<Transaction>
        {
            MakeTrade(buyOrderId: 4001, sellOrderId: 4002, buyerId: 1, sellerId: 2,
                      stockId: 100, qty: 10, price: 5m),
        };

        db.FailNextWrite = FailMode.OnUpdateAllFunds;

        var err = await engine.SettleTradesAsync(trades, orders, ct).ConfigureAwait(false);

        AssertNotNull(err,                            "SettleTradesAsync should report failure");
        AssertEqual(1000m, truMktBuy.BuyBudget,       "BuyBudget restored to original");
    }

    // -----------------------------------------------------------------------
    // Wiring + factories
    // -----------------------------------------------------------------------

    private static (FakeDb db, AccountsCache cache, SettlementEngine engine) BuildEngine(ILoggerFactory factory)
    {
        var db = new FakeDb();
        var cache = new AccountsCache(db, factory.CreateLogger<AccountsCache>());
        var engine = new SettlementEngine(db, cache, new ReservationLedger(), factory.CreateLogger<SettlementEngine>());
        return (db, cache, engine);
    }

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

    private static Dictionary<int, Order> MakeOrderMap(params Order[] orders)
    {
        var d = new Dictionary<int, Order>(orders.Length);
        foreach (var o in orders) d[o.OrderId] = o;
        return d;
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
// Fake IDataBaseService — only implements what SettlementEngine + AccountsCache use.
// Everything else throws so we'd notice if the surface widens.
// =========================================================================

internal enum FailMode { None, OnUpdateAllFunds, OnInsertAllPositions, OnUpdateAllPositions, OnInsertAllTrades }

internal sealed class FakeDb : IDataBaseService
{
    private readonly Dictionary<(int, CurrencyType), Fund> _funds = new();
    private readonly Dictionary<(int, int), Position> _positions = new();
    private int _nextFundId = 1;
    private int _nextPositionId = 1;

    public FailMode FailNextWrite { get; set; } = FailMode.None;
    public int CommittedFundUpdates { get; private set; }
    public int WrittenTradeCount { get; private set; }

    public void SeedFund(int userId, CurrencyType ccy, decimal balance)
    {
        var f = new Fund { UserId = userId, CurrencyType = ccy, TotalBalance = balance };
        f.FundId = _nextFundId++;
        _funds[(userId, ccy)] = f;
    }

    public void SeedPosition(int userId, int stockId, int qty)
    {
        var p = new Position { UserId = userId, StockId = stockId, Quantity = qty };
        p.PositionId = _nextPositionId++;
        _positions[(userId, stockId)] = p;
    }

    // ---- Methods actually used by AccountsCache / SettlementEngine ------------

    public Task<List<Fund>> GetFundsForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        var list = new List<Fund>();
        foreach (var kv in _funds)
            if (userIds.Contains(kv.Key.Item1)) list.Add(kv.Value);
        return Task.FromResult(list);
    }

    public Task<List<Position>> GetPositionsForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        var list = new List<Position>();
        foreach (var kv in _positions)
            if (userIds.Contains(kv.Key.Item1)) list.Add(kv.Value);
        return Task.FromResult(list);
    }

    public Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Task.FromResult<ITransaction>(new FakeTx());

    public Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
    {
        if (typeof(T) == typeof(Transaction))
        {
            if (FailNextWrite == FailMode.OnInsertAllTrades) { FailNextWrite = FailMode.None; throw new InvalidOperationException("fault: OnInsertAllTrades"); }
            foreach (var _ in items) WrittenTradeCount++;
            return Task.CompletedTask;
        }
        if (typeof(T) == typeof(Position))
        {
            if (FailNextWrite == FailMode.OnInsertAllPositions) { FailNextWrite = FailMode.None; throw new InvalidOperationException("fault: OnInsertAllPositions"); }
            foreach (var p in items.Cast<Position>())
                if (p.PositionId == 0) p.PositionId = _nextPositionId++;
            return Task.CompletedTask;
        }
        return Task.CompletedTask; // silently accept other types; widen if SettlementEngine starts using them
    }

    public Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
    {
        if (typeof(T) == typeof(Fund))
        {
            if (FailNextWrite == FailMode.OnUpdateAllFunds) { FailNextWrite = FailMode.None; throw new InvalidOperationException("fault: OnUpdateAllFunds"); }
            foreach (var _ in items) CommittedFundUpdates++;
            return Task.CompletedTask;
        }
        if (typeof(T) == typeof(Position))
        {
            if (FailNextWrite == FailMode.OnUpdateAllPositions) { FailNextWrite = FailMode.None; throw new InvalidOperationException("fault: OnUpdateAllPositions"); }
            return Task.CompletedTask;
        }
        return Task.CompletedTask; // Order updates pass through silently
    }

    private sealed class FakeTx : ITransaction
    {
        public bool IsRoot => true;
        public ValueTask CommitAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RollbackAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- Everything else: throw so widening goes noticed ----------------------

    public Task ResetTableAsync<T>(CancellationToken ct = default) where T : new() => throw new NotImplementedException();
    public Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DropAndRecreateAsync(bool keepBackup = false, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<User>> GetUsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<User> Items, int Total)> GetUsersPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetUserById(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<User?> GetUserByUsername(string username, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<User>> GetUsersByIds(IReadOnlyList<int> userIds, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> UserExists(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateUser(User user, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateUser(User user, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUser(User user, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteUser(User user, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteUserById(int userId, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<Stock>> GetStocksAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Stock?> GetStockById(int stockId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> StockExists(int stockId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateStock(Stock stock, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateStock(Stock stock, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertStock(Stock stock, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteStock(Stock stock, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<Order>> GetOrdersAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<Order> Items, int Total)> GetOrdersPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, string? statusFilter, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Order?> GetOrderById(int orderId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Order>> GetOrdersByIds(List<int> orderIds, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default) => throw new NotImplementedException();
    // AccountsCache.EnsureLoadedAsync calls this to backfill ReservedQuantity from open
    // sell limits; the self-test seeds positions directly so no orders exist yet.
    public Task<List<Order>> GetOpenOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default)
        => Task.FromResult(new List<Order>());
    public Task CreateOrder(Order order, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateOrder(Order order, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteOrder(Order order, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<Transaction> Items, int Total)> GetTransactionsPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateTransaction(Transaction transaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateTransaction(Transaction transaction, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteTransaction(Transaction transaction, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<Position>> GetPositionsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<Position> Items, int Total)> GetPositionsPageAsync(int stockId, int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Position?> GetPositionById(int positionId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreatePosition(Position position, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdatePosition(Position position, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeletePosition(Position position, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertPosition(Position position, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<Fund>> GetFundsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<(List<int> UserIds, int Total)> GetFundsUserIdsPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Fund?> GetFundById(int fundId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateFund(Fund fund, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateFund(Fund fund, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteFund(Fund fund, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertFund(Fund fund, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<FundTransaction>> GetFundTransactionsByUserId(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateFundTransaction(FundTransaction tx, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Candle>> GetCandlesByStockIdAndTimeRange(int stockId, CurrencyType currency, TimeSpan resolution, DateTime from, DateTime to, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateCandle(Candle candle, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateCandle(Candle candle, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteCandle(Candle candle, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertCandle(Candle candle, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<Message>> GetMessagesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Message?> GetMessageById(int messageId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<Message>> GetMessagesByUserId(int userId, bool onlyUnread = false, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateMessage(Message message, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateMessage(Message message, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteMessage(Message message, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> MarkAllMessagesRead(int userId, DateTime? readAtUtc = null, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task CreateAIUser(AIUser aiUser, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default) => throw new NotImplementedException();
    public Task UpsertAIUser(AIUser aiUser, CancellationToken ct = default) => throw new NotImplementedException();
    public Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default) => throw new NotImplementedException();
}
