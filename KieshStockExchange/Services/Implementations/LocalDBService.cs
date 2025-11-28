using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using SQLite;
using System;
using System.Threading;

namespace KieshStockExchange.Services.Implementations;

public class LocalDBService: IDataBaseService, IDisposable
{
    #region Fields and Constructor
    private const string DB_NAME = "localdb.db3";
    private readonly string _dbPath;
    private SQLiteAsyncConnection _db;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly AsyncLocal<Stack<LocalDbTransaction>> _txStack = new();
    private bool _initialized;

    public LocalDBService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, DB_NAME);
        _db = new SQLiteAsyncConnection(_dbPath);
    }
    #endregion

    #region DBTransaction
    private sealed class LocalDbTransaction : ITransaction
    {
        private readonly LocalDBService _owner;
        private readonly SQLiteAsyncConnection _conn;
        private readonly string? _savepoint;
        private bool _completed; // committed or rolled back

        public bool IsRoot { get; }

        public LocalDbTransaction(LocalDBService owner, SQLiteAsyncConnection conn, bool isRoot, string? savepoint)
        {
            _owner = owner;
            _conn = conn;
            IsRoot = isRoot;
            _savepoint = savepoint;
        }

        public async ValueTask CommitAsync(CancellationToken ct = default)
        {
            if (_completed) return;

            if (IsRoot)
                await _conn.ExecuteAsync("COMMIT;");
            else
                await _conn.ExecuteAsync($"RELEASE SAVEPOINT {_savepoint};");

            PopFromStack();
            _completed = true;
        }

        public async ValueTask RollbackAsync(CancellationToken ct = default)
        {
            if (_completed) return;

            if (IsRoot)
            {
                await _conn.ExecuteAsync("ROLLBACK;");
            }
            else
            {
                await _conn.ExecuteAsync($"ROLLBACK TO {_savepoint};");
                await _conn.ExecuteAsync($"RELEASE SAVEPOINT {_savepoint};");
            }

            PopFromStack();
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            // auto-rollback if caller forgot to commit/rollback
            if (!_completed)
                await RollbackAsync();
        }

        private void PopFromStack()
        {
            var stack = _owner._txStack.Value;
            if (stack is { Count: > 0 })
            {
                // pop only if this is the top-most (defensive)
                if (!ReferenceEquals(stack.Peek(), this))
                    throw new InvalidOperationException("Transaction stack out of order.");
                stack.Pop();
            }
        }
    }
    #endregion

    #region Generic operations
    public async Task ResetTableAsync<T>(CancellationToken ct = default) where T : new()
    {
        await InitializeAsync(ct);
        await _db.DropTableAsync<T>();
        await _db.CreateTableAsync<T>();
    }

    public async Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _db.InsertAllAsync(items, runInTransaction: false);
    }

    public async Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default) 
    {
        await InitializeAsync(ct);
        await _db.UpdateAllAsync(items, runInTransaction: false);
    }

    public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        var stack = _txStack.Value ??= new Stack<LocalDbTransaction>();
        var isRoot = stack.Count == 0;

        string? spName = null;
        if (isRoot)
        {
            // take writer lock up-front to avoid deadlocks later
            await _db.ExecuteAsync("BEGIN IMMEDIATE;");
        }
        else
        {
            spName = $"sp_{Guid.NewGuid():N}";
            await _db.ExecuteAsync($"SAVEPOINT {spName};");
        }

        var tx = new LocalDbTransaction(this, _db, isRoot, spName);
        stack.Push(tx);
        return tx;
    }

    public async Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        await using var tx = (LocalDbTransaction)await BeginTransactionAsync(ct);
        try
        {
            await action(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task DropAndRecreateAsync(bool keepBackup = false, CancellationToken ct = default)
    {
        // stop new inits while we reset
        await _initGate.WaitAsync(ct);
        try
        {
            // Close pooled connections so Windows/Android let us delete the file
            try { await _db.CloseAsync(); } catch { /* ignore */ }
            SQLiteAsyncConnection.ResetPool();

            if (keepBackup && File.Exists(_dbPath))
                File.Copy(_dbPath, _dbPath + ".bak", overwrite: true);

            // Remove DB + WAL sidecars (you enable WAL mode at startup) 
            //   localdb.db3, localdb.db3-wal, localdb.db3-shm
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
                if (File.Exists(p)) File.Delete(p);

            // Recreate fresh DB and tables
            _initialized = false;
            _db = new SQLiteAsyncConnection(_dbPath);
            await InitializeAsync(ct); // creates tables + PRAGMAs
        }
        finally { _initGate.Release(); }
    }

    #endregion

    #region User operations
    public async Task<List<User>> GetUsersAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<User>().ToListAsync(), ct);
    }

    public async Task<User?> GetUserById(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<User>().Where(u => u.UserId == userId).FirstOrDefaultAsync(),
            ct);
    }

    public async Task<User?> GetUserByUsername(string username, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<User>().Where(u => u.Username == username).FirstOrDefaultAsync(),
            ct);
    }

    public async Task CreateUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        await RunDbAsync(() => _db.InsertAsync(user), ct);
    }

    public async Task UpdateUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        await RunDbAsync(() => _db.UpdateAsync(user), ct);
    }

    public async Task UpsertUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        await RunDbAsync(() => _db.InsertOrReplaceAsync(user), ct);
    }

    public async Task DeleteUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (user.UserId == 0)
            throw new ArgumentException("User entity must have a valid UserId", nameof(user));
        await RunDbAsync(() => _db.DeleteAsync(user), ct);
    }

    public async Task DeleteUserById(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var user = await GetUserById(userId, ct);
        if (user != null)
            await DeleteUser(user, ct);
    }
    #endregion

    #region Stock operations
    public async Task<List<Stock>> GetStocksAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<Stock>().ToListAsync(), ct);
    }

    public async Task<Stock?> GetStockById(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Stock>().Where(s => s.StockId == stockId).FirstOrDefaultAsync(),
            ct);
    }

    public async Task<bool> StockExists(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var count = await RunDbAsync(() =>
            _db.Table<Stock>().Where(s => s.StockId == stockId).CountAsync(),
            ct);
        return count > 0;
    }

    public async Task CreateStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await RunDbAsync(() => _db.InsertAsync(stock), ct);
    }

    public async Task UpdateStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await RunDbAsync(() => _db.UpdateAsync(stock), ct);
    }

    public async Task UpsertStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await RunDbAsync(() => _db.InsertOrReplaceAsync(stock), ct);
    }

    public async Task DeleteStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (stock.StockId == 0)
            throw new ArgumentException("Stock entity must have a valid StockId", nameof(stock));
        await RunDbAsync(() => _db.DeleteAsync(stock), ct);
    }
    #endregion

    #region StockPrice operations
    public async Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<StockPrice>().ToListAsync(), ct);
    }

    public async Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.PriceId == stockPriceId)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.StockId == stockId)
               .ToListAsync(),
            ct);
    }

    public async Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.StockId == stockId && sp.Currency == currencyCode)
               .OrderByDescending(sp => sp.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.StockId == stockId && sp.Currency == currencyCode && sp.Timestamp <= time)
               .OrderByDescending(sp => sp.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency,
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.StockId == stockId && sp.Timestamp >= from && sp.Timestamp < to && sp.Currency == currencyCode)
               .OrderByDescending(sp => sp.Timestamp)
               .ToListAsync(),
            ct);
    }

    public async Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stockPrice.IsValid())
            throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        await RunDbAsync(() => _db.InsertAsync(stockPrice), ct);
    }

    public async Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stockPrice.IsValid())
            throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        await RunDbAsync(() => _db.UpdateAsync(stockPrice), ct);
    }

    public async Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (stockPrice.PriceId == 0)
            throw new ArgumentException("StockPrice entity must have a valid PriceId", nameof(stockPrice));
        await RunDbAsync(() => _db.DeleteAsync(stockPrice), ct);
    }
    #endregion

    #region Order operations
    public async Task<List<Order>> GetOrdersAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<Order>().ToListAsync(), ct);
    }

    public async Task<Order?> GetOrderById(int orderId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Order>()
               .Where(o => o.OrderId == orderId)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Order>()
               .Where(o => o.UserId == userId)
               .ToListAsync(),
            ct);
    }

    public async Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Order>()
               .Where(o => o.StockId == stockId)
               .ToListAsync(),
            ct);
    }

    public async Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Order>()
               .Where(o => o.StockId == stockId && o.Currency == currencyCode && o.Status == Order.Statuses.Open &&
               (o.OrderType == Order.Types.LimitBuy || o.OrderType == Order.Types.LimitSell)).OrderBy(o => o.CreatedAt)
               .ToListAsync(),
            ct);
    }

    public async Task CreateOrder(Order order, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!order.IsValid())
            throw new ArgumentException("Order entity is not valid", nameof(order));
        await RunDbAsync(() => _db.InsertAsync(order), ct);
    }

    public async Task UpdateOrder(Order order, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!order.IsValid())
            throw new ArgumentException("Order entity is not valid", nameof(order));
        await RunDbAsync(() => _db.UpdateAsync(order), ct);
    }

    public async Task DeleteOrder(Order order, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (order.OrderId == 0)
            throw new ArgumentException("Order entity must have a valid OrderId", nameof(order));
        await RunDbAsync(() => _db.DeleteAsync(order), ct);
    }
    #endregion

    #region Transaction operations
    public async Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<Transaction>().ToListAsync(), ct);
    }

    public async Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.TransactionId == transactionId)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.BuyerId == userId || t.SellerId == userId)
               .ToListAsync(),
            ct);
    }

    public async Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(int stockId, CurrencyType currency,
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.StockId == stockId && t.Timestamp >= from && t.Timestamp < to && t.Currency == currencyCode )
               .ToListAsync(),
            ct);
    }

    public async Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var now = TimeHelper.NowUtc();
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.Timestamp >= since && t.Timestamp <= now)
               .ToListAsync(), ct);
    }

    public async Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.StockId == stockId && t.Currency == currencyCode)
               .OrderByDescending(t => t.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency, 
        DateTime time, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.StockId == stockId && t.Currency == currencyCode && t.Timestamp <= time)
               .OrderByDescending(t => t.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task CreateTransaction(Transaction transaction, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!transaction.IsValid())
            throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        await RunDbAsync(() => _db.InsertAsync(transaction), ct);
    }

    public async Task UpdateTransaction(Transaction transaction, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!transaction.IsValid())
            throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        await RunDbAsync(() => _db.UpdateAsync(transaction), ct);
    }

    public async Task DeleteTransaction(Transaction transaction, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (transaction.TransactionId == 0)
            throw new ArgumentException("Transaction entity must have a valid TransactionId", nameof(transaction));
        await RunDbAsync(() => _db.DeleteAsync(transaction), ct);
    }
    #endregion

    #region Position operations
    public async Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<Position>().ToListAsync(), ct);
    }

    public async Task<Position?> GetPositionById(int positionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Position>()
               .Where(p => p.PositionId == positionId)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Position>()
               .Where(p => p.UserId == userId)
               .ToListAsync(),
            ct);
    }

    public async Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Position>()
               .Where(p => p.UserId == userId && p.StockId == stockId)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task CreatePosition(Position position, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));
        await RunDbAsync(() => _db.InsertAsync(position), ct);
    }

    public async Task UpdatePosition(Position position, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));
        await RunDbAsync(() => _db.UpdateAsync(position), ct);
    }

    public async Task DeletePosition(Position position, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (position.PositionId == 0)
            throw new ArgumentException("Position entity must have a valid PositionId", nameof(position));
        await RunDbAsync(() => _db.DeleteAsync(position), ct);
    }

    public async Task UpsertPosition(Position position, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));

        var existing = await GetPositionByUserIdAndStockId(position.UserId, position.StockId, ct);
        if (existing is not null)
        {
            position.PositionId = existing.PositionId;
            await RunDbAsync(() => _db.UpdateAsync(position), ct);
        }
        else await RunDbAsync(() => _db.InsertAsync(position), ct);
    }
    #endregion

    #region Fund operations
    public async Task<List<Fund>> GetFundsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<Fund>().ToListAsync(), ct);
    }

    public async Task<Fund?> GetFundById(int fundId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Fund>()
               .Where(f => f.FundId == fundId)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Fund>()
               .Where(f => f.UserId == userId)
               .ToListAsync(),
            ct);
    }

    public async Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Fund>()
               .Where(f => f.UserId == userId && f.Currency == currencyCode)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task CreateFund(Fund fund, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await RunDbAsync(() => _db.InsertAsync(fund), ct);
    }

    public async Task UpdateFund(Fund fund, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await RunDbAsync(() => _db.UpdateAsync(fund), ct);
    }

    public async Task DeleteFund(Fund fund, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (fund.FundId == 0)
            throw new ArgumentException("Fund entity must have a valid FundId", nameof(fund));
        await RunDbAsync(() => _db.DeleteAsync(fund), ct);
    }

    public async Task UpsertFund(Fund fund, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));

        var existing = await GetFundByUserIdAndCurrency(fund.UserId, fund.CurrencyType, ct);
        if (existing is not null)
        {
            fund.FundId = existing.FundId;
            await RunDbAsync(() => _db.UpdateAsync(fund), ct);
        }
        else await RunDbAsync(() => _db.InsertAsync(fund), ct);
    }
    #endregion

    #region Candle operations
    public async Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<Candle>().ToListAsync(), ct);
    }

    public async Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Candle>()
               .Where(c => c.CandleId == candleId)
               .FirstOrDefaultAsync(),
            ct);
    }

    public async Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Candle>()
               .Where(c => c.StockId == stockId && c.Currency == currencyCode)
               .ToListAsync(),
            ct);
    }

    public async Task<List<Candle>> GetCandlesByStockIdAndTimeRange(int stockId, CurrencyType currency,
        TimeSpan resolution, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var resolutionSeconds = (int)resolution.TotalSeconds;
        return await RunDbAsync(() =>
            _db.Table<Candle>()
               .Where(c => c.StockId == stockId && c.Currency == currencyCode && c.BucketSeconds == resolutionSeconds
                        && c.OpenTime >= from && c.OpenTime < to)
               .OrderByDescending(c => c.OpenTime)
               .ToListAsync(),
            ct);
    }

    public async Task CreateCandle(Candle candle, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await RunDbAsync(() => _db.InsertAsync(candle), ct);
    }

    public async Task UpdateCandle(Candle candle, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await RunDbAsync(() => _db.UpdateAsync(candle), ct);
    }

    public async Task DeleteCandle(Candle candle, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (candle.CandleId == 0)
            throw new ArgumentException("Candle entity must have a valid CandleId", nameof(candle));
        await RunDbAsync(() => _db.DeleteAsync(candle), ct);
    }

    public async Task UpsertCandle(Candle candle, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));

        // Check for existing candle with same StockId, Currency, Bucket, OpenTime
        var existing = await GetCandlesByStockIdAndTimeRange(candle.StockId, candle.CurrencyType,
            candle.Bucket, candle.OpenTime, candle.CloseTime, ct);

        // There should be at most one match due to uniqueness constraint
        var match = existing.FirstOrDefault(c => c.OpenTime == candle.OpenTime);
        if (match is not null)
        {
            candle.CandleId = match.CandleId;
            await RunDbAsync(() => _db.UpdateAsync(candle), ct);
        }
        else await RunDbAsync(() => _db.InsertAsync(candle), ct);
    }
    #endregion

    #region Message operations
    public async Task<List<Message>> GetMessagesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Message>()
               .OrderByDescending(m => m.CreatedAt)
               .ThenByDescending(m => m.MessageId)
               .ToListAsync(), ct);
    }

    public async Task<Message?> GetMessageById(int messageId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Message>().Where(m => m.MessageId == messageId).FirstOrDefaultAsync(), ct);
    }

    public async Task<List<Message>> GetMessagesByUserId(
        int userId, bool onlyUnread = false, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
        {
            var q = _db.Table<Message>().Where(m => m.UserId == userId);
            if (onlyUnread) q = q.Where(m => m.ReadAt == null);
            q = q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId);
            return q.ToListAsync();
        }, ct);
    }

    public async Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<Message>()
               .Where(m => m.UserId == userId && m.ReadAt == null)
               .CountAsync(), ct);
    }

    public async Task CreateMessage(Message message, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!message.IsValid())
            throw new ArgumentException("Message entity is not valid", nameof(message));
        await RunDbAsync(() => _db.InsertAsync(message), ct);
    }

    public async Task UpdateMessage(Message message, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!message.IsValid())
            throw new ArgumentException("Message entity is not valid", nameof(message));
        await RunDbAsync(() => _db.UpdateAsync(message), ct);
    }

    public async Task DeleteMessage(Message message, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (message.MessageId == 0)
            throw new ArgumentException("Message entity must have a valid MessageId", nameof(message));
        await RunDbAsync(() => _db.DeleteAsync(message), ct);
    }

    public async Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var msg = await GetMessageById(messageId, ct);
        if (msg is null) return false;
        if (!msg.IsRead)
        {
            msg.MarkAsRead();
            await RunDbAsync(() => _db.UpdateAsync(msg), ct);
        }
        return true;
    }

    public async Task<int> MarkAllMessagesRead(int userId, DateTime? readAtUtc = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var when = readAtUtc ?? TimeHelper.NowUtc();
        return await RunDbAsync(() =>
            _db.ExecuteAsync("UPDATE Messages SET ReadAt = ? WHERE UserId = ? AND ReadAt IS NULL", when, userId), ct);
    }
    #endregion

    #region AIUser operations
    public async Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() => _db.Table<AIUser>().ToListAsync(), ct);
    }

    public async Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<AIUser>().Where(a => a.AiUserId == aiUserId).FirstOrDefaultAsync(), ct);
    }

    public async Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<AIUser>().Where(a => a.UserId == userId).ToListAsync(), ct);
    }

    public async Task CreateAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!aiUser.IsValid())
            throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));
        await RunDbAsync(() => _db.InsertAsync(aiUser), ct);
    }

    public async Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!aiUser.IsValid())
            throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));
        await RunDbAsync(() => _db.UpdateAsync(aiUser), ct);
    }

    public async Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (aiUser.AiUserId == 0)
            throw new ArgumentException("AIUser entity must have a valid AiUserId", nameof(aiUser));
        await RunDbAsync(() => _db.DeleteAsync(aiUser), ct);
    }

    public async Task UpsertAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!aiUser.IsValid())
            throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));

        var existing = await GetAIUserById(aiUser.AiUserId, ct);
        if (existing is not null)
        {
            aiUser.AiUserId = existing.AiUserId;
            await RunDbAsync(() => _db.UpdateAsync(aiUser), ct);
        }
        else await RunDbAsync(() => _db.InsertAsync(aiUser), ct);
    }
    #endregion

    #region Helper Methods
    private async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        // Support cancellation while waiting for the lock
        await _initGate.WaitAsync(ct);

        try
        {
            if (_initialized) return;

            await Pragma();

            await _db.CreateTableAsync<User>();
            await _db.CreateTableAsync<Stock>();
            await _db.CreateTableAsync<StockPrice>();
            await _db.CreateTableAsync<Order>();
            await _db.CreateTableAsync<Transaction>();
            await _db.CreateTableAsync<Position>();
            await _db.CreateTableAsync<Fund>();
            await _db.CreateTableAsync<Candle>();
            await _db.CreateTableAsync<Message>();
            await _db.CreateTableAsync<AIUser>();

            _initialized = true;
        }
        finally { _initGate.Release(); }
    }

    private static Task<T> RunDbAsync<T>(Func<Task<T>> action, CancellationToken ct) =>
        Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            return await action();
        }, ct);

    private static Task RunDbAsync(Func<Task> action, CancellationToken ct) =>
        Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            await action();
        }, ct);

    public void Dispose() { _db.GetConnection().Dispose(); }

    private async Task Pragma()
    {
        // Tells SQLite to actually enforce FOREIGN KEY constraints
        await _db.ExecuteScalarAsync<string>("PRAGMA foreign_keys = ON;");

        // Switch to Write-Ahead Logging, so readers don’t block writers
        await _db.ExecuteScalarAsync<string>("PRAGMA journal_mode = WAL;");

        // Reduces fsyncs compared to FULL, giving faster writes
        await _db.ExecuteScalarAsync<string>("PRAGMA synchronous = NORMAL;");

        // Wait up to max 5s on writer lock
        await _db.ExecuteScalarAsync<string>("PRAGMA busy_timeout = 5000;"); 
    }
    #endregion
}
