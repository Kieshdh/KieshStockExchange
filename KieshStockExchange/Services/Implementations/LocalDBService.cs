using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using SQLite;
using System.Threading;

namespace KieshStockExchange.Services.Implementations;

public class LocalDBService: IDataBaseService
{
    #region Fields and Constructor
    private const string DB_NAME = "localdb.db3";
    private readonly SQLiteAsyncConnection _db;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private static readonly AsyncLocal<Stack<string>> _txSavepointStack = new();
    private bool _initialized;

    public LocalDBService()
    {
        string dbPath = Path.Combine(FileSystem.AppDataDirectory, DB_NAME);
        _db = new SQLiteAsyncConnection(dbPath);
        _ = InitializePragmasAsync();
    }
    #endregion

    #region Generic operations
    public async Task ResetTableAsync<T>(CancellationToken cancellationToken = default) where T : new()
    {
        await InitializeAsync(cancellationToken);
        await RunDbAsync(() => _db.DropTableAsync<T>(), cancellationToken);
        await RunDbAsync(() => _db.CreateTableAsync<T>(), cancellationToken);
    }

    public async Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await RunDbAsync(() => _db.InsertAllAsync(items), ct);
    }


    public async Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default) 
    {
        await InitializeAsync(ct);
        await RunDbAsync(() => _db.UpdateAllAsync(items), ct);
    }

    public async Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        var stack = _txSavepointStack.Value ??= new Stack<string>();
        var isOuter = stack.Count == 0;
        string? spName = null;

        try
        {
            // --- open transaction (or savepoint for nesting) ---
            if (isOuter)
            {
                // IMMEDIATE: take a RESERVED lock now → avoids deadlocks later on first write
                await _db.ExecuteAsync("BEGIN IMMEDIATE;");
            }
            else
            {
                spName = $"sp_{Guid.NewGuid():N}";
                stack.Push(spName);
                await _db.ExecuteAsync($"SAVEPOINT {spName};");
            }

            // --- run the user's work inside the transaction ---
            cancellationToken.ThrowIfCancellationRequested();
            await action(cancellationToken);

            // --- commit/release ---
            if (isOuter)
            {
                await _db.ExecuteAsync("COMMIT;");
            }
            else
            {
                await _db.ExecuteAsync($"RELEASE SAVEPOINT {spName};");
                stack.Pop();
            }
        }
        catch (OperationCanceledException)
        {
            // --- rollback on cancel ---
            if (isOuter)
            {
                await _db.ExecuteAsync("ROLLBACK;");
            }
            else if (spName != null)
            {
                await _db.ExecuteAsync($"ROLLBACK TO {spName};");
                await _db.ExecuteAsync($"RELEASE SAVEPOINT {spName};");
                if (stack.Count > 0 && stack.Peek() == spName) stack.Pop();
            }
            throw; // bubble cancellation up
        }
        catch
        {
            // --- rollback on any error ---
            if (isOuter)
            {
                await _db.ExecuteAsync("ROLLBACK;");
            }
            else if (spName != null)
            {
                await _db.ExecuteAsync($"ROLLBACK TO {spName};");
                await _db.ExecuteAsync($"RELEASE SAVEPOINT {spName};");
                if (stack.Count > 0 && stack.Peek() == spName) stack.Pop();
            }
            throw; // bubble the exception up
        }
    }
    #endregion

    #region User operations
    public async Task<List<User>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<User>().ToListAsync(), cancellationToken);
    }

    public async Task<User?> GetUserById(int userId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<User>().Where(u => u.UserId == userId).FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<User?> GetUserByUsername(string username, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<User>().Where(u => u.Username == username).FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task CreateUser(User user, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        await RunDbAsync(() => _db.InsertAsync(user), cancellationToken);
    }

    public async Task UpdateUser(User user, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        await RunDbAsync(() => _db.UpdateAsync(user), cancellationToken);
    }

    public async Task DeleteUser(User user, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (user.UserId == 0)
            throw new ArgumentException("User entity must have a valid UserId", nameof(user));
        await RunDbAsync(() => _db.DeleteAsync(user), cancellationToken);
    }

    public async Task DeleteUserById(int userId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var user = await GetUserById(userId, cancellationToken);
        if (user != null)
            await DeleteUser(user, cancellationToken);
    }
    #endregion

    #region Stock operations
    public async Task<List<Stock>> GetStocksAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<Stock>().ToListAsync(), cancellationToken);
    }

    public async Task<Stock?> GetStockById(int stockId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Stock>().Where(s => s.StockId == stockId).FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<bool> StockExists(int stockId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var count = await RunDbAsync(() =>
            _db.Table<Stock>().Where(s => s.StockId == stockId).CountAsync(),
            cancellationToken);
        return count > 0;
    }

    public async Task CreateStock(Stock stock, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await RunDbAsync(() => _db.InsertAsync(stock), cancellationToken);
    }

    public async Task UpdateStock(Stock stock, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await RunDbAsync(() => _db.UpdateAsync(stock), cancellationToken);
    }

    public async Task DeleteStock(Stock stock, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (stock.StockId == 0)
            throw new ArgumentException("Stock entity must have a valid StockId", nameof(stock));
        await RunDbAsync(() => _db.DeleteAsync(stock), cancellationToken);
    }
    #endregion

    #region StockPrice operations
    public async Task<List<StockPrice>> GetStockPricesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<StockPrice>().ToListAsync(), cancellationToken);
    }

    public async Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.PriceId == stockPriceId)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.StockId == stockId)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.StockId == stockId && sp.Currency == currencyCode)
               .OrderByDescending(sp => sp.Timestamp)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency,
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<StockPrice>()
               .Where(sp => sp.StockId == stockId && sp.Timestamp >= from && sp.Timestamp <= to && sp.Currency == currencyCode)
               .OrderByDescending(sp => sp.Timestamp)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task CreateStockPrice(StockPrice stockPrice, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!stockPrice.IsValid())
            throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        await RunDbAsync(() => _db.InsertAsync(stockPrice), cancellationToken);
    }

    public async Task UpdateStockPrice(StockPrice stockPrice, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!stockPrice.IsValid())
            throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        await RunDbAsync(() => _db.UpdateAsync(stockPrice), cancellationToken);
    }

    public async Task DeleteStockPrice(StockPrice stockPrice, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (stockPrice.PriceId == 0)
            throw new ArgumentException("StockPrice entity must have a valid PriceId", nameof(stockPrice));
        await RunDbAsync(() => _db.DeleteAsync(stockPrice), cancellationToken);
    }
    #endregion

    #region Order operations
    public async Task<List<Order>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<Order>().ToListAsync(), cancellationToken);
    }

    public async Task<Order?> GetOrderById(int orderId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Order>()
               .Where(o => o.OrderId == orderId)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Order>()
               .Where(o => o.UserId == userId)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Order>()
               .Where(o => o.StockId == stockId)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task CreateOrder(Order order, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!order.IsValid())
            throw new ArgumentException("Order entity is not valid", nameof(order));
        await RunDbAsync(() => _db.InsertAsync(order), cancellationToken);
    }

    public async Task UpdateOrder(Order order, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!order.IsValid())
            throw new ArgumentException("Order entity is not valid", nameof(order));
        await RunDbAsync(() => _db.UpdateAsync(order), cancellationToken);
    }

    public async Task DeleteOrder(Order order, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (order.OrderId == 0)
            throw new ArgumentException("Order entity must have a valid OrderId", nameof(order));
        await RunDbAsync(() => _db.DeleteAsync(order), cancellationToken);
    }
    #endregion

    #region Transaction operations
    public async Task<List<Transaction>> GetTransactionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<Transaction>().ToListAsync(), cancellationToken);
    }

    public async Task<Transaction?> GetTransactionById(int transactionId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.TransactionId == transactionId)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.BuyerId == userId || t.SellerId == userId)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(int stockId, CurrencyType currency,
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.StockId == stockId && t.Timestamp >= from && t.Timestamp <= to && t.Currency == currencyCode )
               .ToListAsync(),
            cancellationToken);
    }

    public async Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Transaction>()
               .Where(t => t.StockId == stockId && t.Currency == currencyCode)
               .OrderByDescending(t => t.Timestamp)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task CreateTransaction(Transaction transaction, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!transaction.IsValid())
            throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        await RunDbAsync(() => _db.InsertAsync(transaction), cancellationToken);
    }

    public async Task UpdateTransaction(Transaction transaction, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!transaction.IsValid())
            throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        await RunDbAsync(() => _db.UpdateAsync(transaction), cancellationToken);
    }

    public async Task DeleteTransaction(Transaction transaction, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (transaction.TransactionId == 0)
            throw new ArgumentException("Transaction entity must have a valid TransactionId", nameof(transaction));
        await RunDbAsync(() => _db.DeleteAsync(transaction), cancellationToken);
    }
    #endregion

    #region Position operations
    public async Task<List<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<Position>().ToListAsync(), cancellationToken);
    }

    public async Task<Position?> GetPositionById(int positionId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Position>()
               .Where(p => p.PositionId == positionId)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Position>()
               .Where(p => p.UserId == userId)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Position>()
               .Where(p => p.UserId == userId && p.StockId == stockId)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task CreatePosition(Position position, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));
        await RunDbAsync(() => _db.InsertAsync(position), cancellationToken);
    }

    public async Task UpdatePosition(Position position, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));
        await RunDbAsync(() => _db.UpdateAsync(position), cancellationToken);
    }

    public async Task DeletePosition(Position position, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (position.PositionId == 0)
            throw new ArgumentException("Position entity must have a valid PositionId", nameof(position));
        await RunDbAsync(() => _db.DeleteAsync(position), cancellationToken);
    }

    public async Task UpsertPosition(Position position, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));
        await RunDbAsync(() => _db.InsertOrReplaceAsync(position), cancellationToken);
    }
    #endregion

    #region Fund operations
    public async Task<List<Fund>> GetFundsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<Fund>().ToListAsync(), cancellationToken);
    }

    public async Task<Fund?> GetFundById(int fundId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Fund>()
               .Where(f => f.FundId == fundId)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Fund>()
               .Where(f => f.UserId == userId)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Fund>()
               .Where(f => f.UserId == userId && f.Currency == currencyCode)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task CreateFund(Fund fund, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await RunDbAsync(() => _db.InsertAsync(fund), cancellationToken);
    }

    public async Task UpdateFund(Fund fund, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await RunDbAsync(() => _db.UpdateAsync(fund), cancellationToken);
    }

    public async Task DeleteFund(Fund fund, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (fund.FundId == 0)
            throw new ArgumentException("Fund entity must have a valid FundId", nameof(fund));
        await RunDbAsync(() => _db.DeleteAsync(fund), cancellationToken);
    }

    public async Task UpsertFund(Fund fund, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await RunDbAsync(() => _db.InsertOrReplaceAsync(fund), cancellationToken);
    }
    #endregion

    #region Candle operations
    public async Task<List<Candle>> GetCandlesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() => _db.Table<Candle>().ToListAsync(), cancellationToken);
    }

    public async Task<Candle?> GetCandleById(int candleId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await RunDbAsync(() =>
            _db.Table<Candle>()
               .Where(c => c.CandleId == candleId)
               .FirstOrDefaultAsync(),
            cancellationToken);
    }

    public async Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var currencyCode = currency.ToString();
        return await RunDbAsync(() =>
            _db.Table<Candle>()
               .Where(c => c.StockId == stockId && c.Currency == currencyCode)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task<List<Candle>> GetCandlesByStockIdAndTimeRange(int stockId, CurrencyType currency,
        TimeSpan resolution, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var currencyCode = currency.ToString();
        var resolutionSeconds = (int)resolution.TotalSeconds;
        return await RunDbAsync(() =>
            _db.Table<Candle>()
               .Where(c => c.StockId == stockId && c.Currency == currencyCode && c.ResolutionSeconds == resolutionSeconds
                        && c.OpenTime >= from && c.OpenTime <= to)
               .OrderByDescending(c => c.OpenTime)
               .ToListAsync(),
            cancellationToken);
    }

    public async Task CreateCandle(Candle candle, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await RunDbAsync(() => _db.InsertAsync(candle), cancellationToken);
    }

    public async Task UpdateCandle(Candle candle, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await RunDbAsync(() => _db.UpdateAsync(candle), cancellationToken);
    }

    public async Task DeleteCandle(Candle candle, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (candle.CandleId == 0)
            throw new ArgumentException("Candle entity must have a valid CandleId", nameof(candle));
        await RunDbAsync(() => _db.DeleteAsync(candle), cancellationToken);
    }

    public async Task UpsertCandle(Candle candle, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await RunDbAsync(() => _db.InsertOrReplaceAsync(candle), cancellationToken);
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

            // Wrap each DB call so cancellation is checked before
            ct.ThrowIfCancellationRequested();

            await _db.CreateTableAsync<User>();
            await _db.CreateTableAsync<Stock>();
            await _db.CreateTableAsync<StockPrice>();
            await _db.CreateTableAsync<Order>();
            await _db.CreateTableAsync<Transaction>();
            await _db.CreateTableAsync<Position>();
            await _db.CreateTableAsync<Fund>();
            await _db.CreateTableAsync<Candle>();

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
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

    private async Task InitializePragmasAsync()
    {
        // These improve reliability under concurrency and ensure constraints are enforced.
        await _db.ExecuteAsync("PRAGMA foreign_keys = ON;");
        await _db.ExecuteAsync("PRAGMA journal_mode = WAL;");     // better concurrent reads
        await _db.ExecuteAsync("PRAGMA synchronous = NORMAL;");
        await _db.ExecuteAsync("PRAGMA busy_timeout = 5000;");    // wait up to 5s on writer lock
    }
    #endregion
}
