using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using SQLite;
using System;
using System.Diagnostics;
using System.Threading;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Services.DataServices;

public class LocalDBService: IDataBaseService, IDisposable
{
    #region Fields and Constructor
    private const string DB_NAME = "localdb.db3";
    private readonly string _dbPath;
    private SQLiteAsyncConnection _db;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    // Serializes root SQLite transactions across the shared connection. Without this, two
    // parallel async flows (e.g. Phase 3 of PlaceAndMatchBatchAsync) each see an empty
    // _txStack, both decide they are root, and both issue BEGIN IMMEDIATE → SQLite throws
    // "cannot start a transaction within a transaction".
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly AsyncLocal<Stack<LocalDbTransaction>> _txStack = new();
    private readonly ILogger<LocalDBService>? _logger;
    private bool _initialized;

    // Threshold for the diagnostic warning when _writeGate wait time gets long enough
    // to plausibly explain a hung modify-Confirm modal. Anything under this is normal
    // single-writer SQLite contention.
    private const int WriteGateWaitWarnMs = 100;

    public LocalDBService(ILogger<LocalDBService>? logger = null)
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, DB_NAME);
        _db = new SQLiteAsyncConnection(_dbPath);
        _logger = logger;
    }
    #endregion

    #region DBTransaction
    private sealed class LocalDbTransaction : ITransaction
    {
        private readonly LocalDBService _owner;
        private readonly SQLiteAsyncConnection _conn;
        private readonly string? _savepoint;
        private bool _completed; // committed or rolled back
        // 0 = writer gate still held by this root tx; 1 = released. Only meaningful when IsRoot.
        // Atomic guard so Commit-then-Dispose (which calls Rollback) can't double-release.
        private int _gateReleased;

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
            try
            {
                if (IsRoot)
                    await _conn.ExecuteAsync("COMMIT;");
                else
                    await _conn.ExecuteAsync($"RELEASE SAVEPOINT {_savepoint};");
            }
            finally
            {
                PopFromStack();
                _completed = true;
                if (IsRoot && Interlocked.Exchange(ref _gateReleased, 1) == 0)
                    _owner._writeGate.Release();
            }
        }

        public async ValueTask RollbackAsync(CancellationToken ct = default)
        {
            if (_completed) return;
            try
            {
                if (IsRoot)
                {
                    await _conn.ExecuteAsync("ROLLBACK;");
                }
                else
                {
                    await _conn.ExecuteAsync($"ROLLBACK TO {_savepoint};");
                    await _conn.ExecuteAsync($"RELEASE SAVEPOINT {_savepoint};");
                }
            }
            finally
            {
                PopFromStack();
                _completed = true;
                if (IsRoot && Interlocked.Exchange(ref _gateReleased, 1) == 0)
                    _owner._writeGate.Release();
            }
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
            // Hold _writeGate across BEGIN..COMMIT so only one root tx exists on the
            // shared connection at a time. Released in CommitAsync/RollbackAsync.
            // Diagnostic: timing the wait surfaces _writeGate contention. Long waits
            // here are the smoking gun for hung UI operations (modify Confirm, etc.)
            // when the bot batch holds the gate for an entire Phase 3 sweep.
            var sw = Stopwatch.StartNew();
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            sw.Stop();
            if (sw.ElapsedMilliseconds > WriteGateWaitWarnMs)
                _logger?.LogWarning(
                    "BeginTransaction waited {ElapsedMs}ms for _writeGate.",
                    sw.ElapsedMilliseconds);
            try
            {
                await _db.ExecuteAsync("BEGIN IMMEDIATE;");
            }
            catch
            {
                _writeGate.Release();
                throw;
            }
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
        var rows = await RunDbAsync(() => _db.Table<UserRow>().ToListAsync(), ct);
        return rows.Select(UserMapper.ToDomain).ToList();
    }

    public async Task<(List<User> Items, int Total)> GetUsersPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(async () =>
        {
            var q = _db.Table<UserRow>();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (int.TryParse(filter.Trim(), out var id))
                    q = q.Where(u => u.UserId == id);
                else
                {
                    string f = filter.Trim();
                    q = q.Where(u => u.Username.Contains(f)
                                   || u.Email.Contains(f)
                                   || u.FullName.Contains(f));
                }
            }
            var total = await q.CountAsync();
            var ordered = (sortKey, desc) switch
            {
                ("Username",  true)  => q.OrderByDescending(u => u.Username),
                ("Username",  false) => q.OrderBy(u => u.Username),
                ("Email",     true)  => q.OrderByDescending(u => u.Email),
                ("Email",     false) => q.OrderBy(u => u.Email),
                ("FullName",  true)  => q.OrderByDescending(u => u.FullName),
                ("FullName",  false) => q.OrderBy(u => u.FullName),
                ("BirthDate", true)  => q.OrderByDescending(u => u.BirthDate),
                ("BirthDate", false) => q.OrderBy(u => u.BirthDate),
                ("UserId",    true)  => q.OrderByDescending(u => u.UserId),
                ("UserId",    false) => q.OrderBy(u => u.UserId),
                (_,           true)  => q.OrderByDescending(u => u.CreatedAt),
                (_,           false) => q.OrderBy(u => u.CreatedAt),
            };
            var rows = await ordered.Skip(skip).Take(take).ToListAsync();
            return (rows.Select(UserMapper.ToDomain).ToList(), total);
        }, ct);
    }

    public async Task<User?> GetUserById(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<UserRow>().Where(u => u.UserId == userId).FirstOrDefaultAsync(),
            ct);
        return row is null ? null : UserMapper.ToDomain(row);
    }

    public async Task<User?> GetUserByUsername(string username, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<UserRow>().Where(u => u.Username == username).FirstOrDefaultAsync(),
            ct);
        return row is null ? null : UserMapper.ToDomain(row);
    }

    public async Task<List<User>> GetUsersByIds(IReadOnlyList<int> userIds, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (userIds is null || userIds.Count == 0) return new List<User>();

        // Deduplicate so the IN-clause query stays small even if the caller passes a noisy
        // list (e.g., orders where many rows share the same UserId).
        var distinct = userIds.Distinct().ToList();

        // ChunkedContainsAsync handles the SQLite ~999-variable limit; each chunk
        // is a single round-trip and the helper concatenates results.
        var rows = await ChunkedContainsAsync(distinct, chunk => RunDbAsync(() =>
            _db.Table<UserRow>().Where(u => chunk.Contains(u.UserId)).ToListAsync(), ct), ct);
        return rows.Select(UserMapper.ToDomain).ToList();
    }

    public async Task<bool> UserExists(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var count = await RunDbAsync(() =>
            _db.Table<UserRow>().Where(u => u.UserId == userId).CountAsync(),
            ct);
        return count > 0;
    }

    public async Task CreateUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        var row = UserMapper.ToRow(user);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        user.UserId = row.UserId;
    }

    public async Task UpdateUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        await RunDbAsync(() => _db.UpdateAsync(UserMapper.ToRow(user)), ct);
    }

    public async Task UpsertUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!user.IsValid())
            throw new ArgumentException("User entity is not valid", nameof(user));
        await RunDbAsync(() => _db.InsertOrReplaceAsync(UserMapper.ToRow(user)), ct);
    }

    public async Task DeleteUser(User user, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (user.UserId == 0)
            throw new ArgumentException("User entity must have a valid UserId", nameof(user));
        await RunDbAsync(() => _db.DeleteAsync(UserMapper.ToRow(user)), ct);
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
        var rows = await RunDbAsync(() => _db.Table<StockRow>().ToListAsync(), ct);
        return rows.Select(StockMapper.ToDomain).ToList();
    }

    public async Task<Stock?> GetStockById(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<StockRow>().Where(s => s.StockId == stockId).FirstOrDefaultAsync(),
            ct);
        return row is null ? null : StockMapper.ToDomain(row);
    }

    public async Task<bool> StockExists(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var count = await RunDbAsync(() =>
            _db.Table<StockRow>().Where(s => s.StockId == stockId).CountAsync(),
            ct);
        return count > 0;
    }

    public async Task CreateStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        var row = StockMapper.ToRow(stock);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        stock.StockId = row.StockId; // propagate auto-assigned PK
    }

    public async Task UpdateStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await RunDbAsync(() => _db.UpdateAsync(StockMapper.ToRow(stock)), ct);
    }

    public async Task UpsertStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stock.IsValid())
            throw new ArgumentException("Stock entity is not valid", nameof(stock));
        await RunDbAsync(() => _db.InsertOrReplaceAsync(StockMapper.ToRow(stock)), ct);
    }

    public async Task DeleteStock(Stock stock, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (stock.StockId == 0)
            throw new ArgumentException("Stock entity must have a valid StockId", nameof(stock));
        await RunDbAsync(() => _db.DeleteAsync(StockMapper.ToRow(stock)), ct);
    }
    #endregion

    #region StockListing operations
    public async Task<List<StockListing>> GetStockListingsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<StockListingRow>().ToListAsync(), ct);
        return rows.Select(StockListingMapper.ToDomain).ToList();
    }

    public async Task<List<StockListing>> GetStockListingsByStockId(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<StockListingRow>().Where(l => l.StockId == stockId).ToListAsync(), ct);
        return rows.Select(StockListingMapper.ToDomain).ToList();
    }

    public async Task CreateStockListing(StockListing listing, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!listing.IsValid())
            throw new ArgumentException("StockListing entity is not valid", nameof(listing));
        var row = StockListingMapper.ToRow(listing);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        listing.ListingId = row.ListingId;
    }
    #endregion

    #region StockPrice operations
    public async Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<StockPriceRow>().ToListAsync(), ct);
        return rows.Select(StockPriceMapper.ToDomain).ToList();
    }

    public async Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<StockPriceRow>()
               .Where(sp => sp.PriceId == stockPriceId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : StockPriceMapper.ToDomain(row);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<StockPriceRow>()
               .Where(sp => sp.StockId == stockId)
               .ToListAsync(),
            ct);
        return rows.Select(StockPriceMapper.ToDomain).ToList();
    }

    public async Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var row = await RunDbAsync(() =>
            _db.Table<StockPriceRow>()
               .Where(sp => sp.StockId == stockId && sp.Currency == currencyCode)
               .OrderByDescending(sp => sp.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : StockPriceMapper.ToDomain(row);
    }

    public async Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var row = await RunDbAsync(() =>
            _db.Table<StockPriceRow>()
               .Where(sp => sp.StockId == stockId && sp.Currency == currencyCode && sp.Timestamp <= time)
               .OrderByDescending(sp => sp.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : StockPriceMapper.ToDomain(row);
    }

    public async Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency,
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var rows = await RunDbAsync(() =>
            _db.Table<StockPriceRow>()
               .Where(sp => sp.StockId == stockId && sp.Timestamp >= from && sp.Timestamp < to && sp.Currency == currencyCode)
               .OrderByDescending(sp => sp.Timestamp)
               .ToListAsync(),
            ct);
        return rows.Select(StockPriceMapper.ToDomain).ToList();
    }

    public async Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stockPrice.IsValid())
            throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        var row = StockPriceMapper.ToRow(stockPrice);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        stockPrice.PriceId = row.PriceId;
    }

    public async Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!stockPrice.IsValid())
            throw new ArgumentException("StockPrice entity is not valid", nameof(stockPrice));
        await RunDbAsync(() => _db.UpdateAsync(StockPriceMapper.ToRow(stockPrice)), ct);
    }

    public async Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (stockPrice.PriceId == 0)
            throw new ArgumentException("StockPrice entity must have a valid PriceId", nameof(stockPrice));
        await RunDbAsync(() => _db.DeleteAsync(StockPriceMapper.ToRow(stockPrice)), ct);
    }
    #endregion

    #region Order operations
    public async Task<List<Order>> GetOrdersAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<OrderRow>().ToListAsync(), ct);
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<(List<Order> Items, int Total)> GetOrdersPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, string? statusFilter, int? userIdFilter = null, int? stockIdFilter = null, string? sideFilter = null, string? typeFilter = null, IList<int>? excludeUserIds = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(async () =>
        {
            var q = _db.Table<OrderRow>().Where(o => o.CreatedAt >= fromUtc && o.CreatedAt <= toUtc);
            if (excludeUserIds is { Count: > 0 })
            {
                var excl = excludeUserIds; // SQLite-Net: .Contains() compiles to IN; negation yields NOT IN.
                q = q.Where(o => !excl.Contains(o.UserId));
            }
            if (!string.IsNullOrWhiteSpace(statusFilter))
                q = q.Where(o => o.Status == statusFilter);
            if (userIdFilter.HasValue)
            {
                var uid = userIdFilter.Value;
                q = q.Where(o => o.UserId == uid);
            }
            if (stockIdFilter.HasValue)
            {
                var sid = stockIdFilter.Value;
                q = q.Where(o => o.StockId == sid);
            }
            if (!string.IsNullOrWhiteSpace(sideFilter))
            {
                // SQLite-Net only translates equality, so enumerate matching OrderType values.
                if (string.Equals(sideFilter, "Buy", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(o => o.OrderType == Order.Types.LimitBuy
                                   || o.OrderType == Order.Types.TrueMarketBuy
                                   || o.OrderType == Order.Types.SlippageMarketBuy);
                else if (string.Equals(sideFilter, "Sell", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(o => o.OrderType == Order.Types.LimitSell
                                   || o.OrderType == Order.Types.TrueMarketSell
                                   || o.OrderType == Order.Types.SlippageMarketSell);
            }
            if (!string.IsNullOrWhiteSpace(typeFilter))
            {
                if (string.Equals(typeFilter, "Limit", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(o => o.OrderType == Order.Types.LimitBuy
                                   || o.OrderType == Order.Types.LimitSell);
                else if (string.Equals(typeFilter, "Market", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(o => o.OrderType == Order.Types.TrueMarketBuy
                                   || o.OrderType == Order.Types.TrueMarketSell
                                   || o.OrderType == Order.Types.SlippageMarketBuy
                                   || o.OrderType == Order.Types.SlippageMarketSell);
            }
            var total = await q.CountAsync();
            var ordered = (sortKey, desc) switch
            {
                ("OrderId",  true)  => q.OrderByDescending(o => o.OrderId),
                ("OrderId",  false) => q.OrderBy(o => o.OrderId),
                ("UserId",   true)  => q.OrderByDescending(o => o.UserId),
                ("UserId",   false) => q.OrderBy(o => o.UserId),
                ("StockId",  true)  => q.OrderByDescending(o => o.StockId),
                ("StockId",  false) => q.OrderBy(o => o.StockId),
                ("Quantity", true)  => q.OrderByDescending(o => o.Quantity),
                ("Quantity", false) => q.OrderBy(o => o.Quantity),
                (_,          true)  => q.OrderByDescending(o => o.CreatedAt),
                (_,          false) => q.OrderBy(o => o.CreatedAt),
            };
            var rows = await ordered.Skip(skip).Take(take).ToListAsync();
            return (rows.Select(OrderMapper.ToDomain).ToList(), total);
        }, ct);
    }

    public async Task<Order?> GetOrderById(int orderId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<OrderRow>()
               .Where(o => o.OrderId == orderId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : OrderMapper.ToDomain(row);
    }

    public async Task<List<Order>> GetOrdersByIds(List<int> orderIds, CancellationToken ct = default)
    {
        if (orderIds is null || orderIds.Count == 0) return new List<Order>();
        await InitializeAsync(ct);
        var rows = await ChunkedContainsAsync(orderIds, chunk => RunDbAsync(() =>
            _db.Table<OrderRow>().Where(o => chunk.Contains(o.OrderId)).ToListAsync(), ct), ct);
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    /// <summary>
    /// SQLite caps a single statement at ~999 variables, and SQLite-net translates
    /// <c>List.Contains(column)</c> into an <c>IN (?, ?, ...)</c> clause. Any caller
    /// that may pass more than ~500 ids must funnel through here so the IN-list is
    /// chunked. Returns the concatenation of per-chunk query results in input order.
    /// </summary>
    private static async Task<List<T>> ChunkedContainsAsync<T>(
        List<int> ids, Func<List<int>, Task<List<T>>> chunkQuery, CancellationToken ct,
        int chunkSize = 500)
    {
        if (ids.Count <= chunkSize) return await chunkQuery(ids).ConfigureAwait(false);

        var result = new List<T>(ids.Count);
        for (int start = 0; start < ids.Count; start += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(chunkSize, ids.Count - start);
            var chunk = ids.GetRange(start, take);
            var rows = await chunkQuery(chunk).ConfigureAwait(false);
            result.AddRange(rows);
        }
        return result;
    }

    public async Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<OrderRow>()
               .Where(o => o.UserId == userId)
               .ToListAsync(),
            ct);
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<OrderRow>()
               .Where(o => o.StockId == stockId)
               .ToListAsync(),
            ct);
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var rows = await RunDbAsync(() =>
            _db.Table<OrderRow>()
               .Where(o => o.StockId == stockId && o.Currency == currencyCode && o.Status == Order.Statuses.Open &&
               (o.OrderType == Order.Types.LimitBuy || o.OrderType == Order.Types.LimitSell)).OrderBy(o => o.CreatedAt)
               .ToListAsync(),
            ct);
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task<List<Order>> GetOpenOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<Order>();
        await InitializeAsync(ct);
        var rows = await ChunkedContainsAsync(userIds, chunk => RunDbAsync(() =>
            _db.Table<OrderRow>()
               .Where(o => chunk.Contains(o.UserId) && o.Status == Order.Statuses.Open &&
                     (o.OrderType == Order.Types.LimitBuy || o.OrderType == Order.Types.LimitSell))
               .ToListAsync(), ct), ct);
        return rows.Select(OrderMapper.ToDomain).ToList();
    }

    public async Task CreateOrder(Order order, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!order.IsValid())
            throw new ArgumentException("Order entity is not valid", nameof(order));
        var row = OrderMapper.ToRow(order);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        order.OrderId = row.OrderId;
    }

    public async Task UpdateOrder(Order order, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!order.IsValid())
            throw new ArgumentException("Order entity is not valid", nameof(order));
        await RunDbAsync(() => _db.UpdateAsync(OrderMapper.ToRow(order)), ct);
    }

    public async Task DeleteOrder(Order order, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (order.OrderId == 0)
            throw new ArgumentException("Order entity must have a valid OrderId", nameof(order));
        await RunDbAsync(() => _db.DeleteAsync(OrderMapper.ToRow(order)), ct);
    }
    #endregion

    #region Transaction operations
    public async Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<TransactionRow>().ToListAsync(), ct);
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<(List<Transaction> Items, int Total)> GetTransactionsPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, int? userIdFilter = null, int? stockIdFilter = null, string? currencyFilter = null, IList<int>? excludeBuyerOrSellerIds = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(async () =>
        {
            var q = _db.Table<TransactionRow>().Where(t => t.Timestamp >= fromUtc && t.Timestamp <= toUtc);
            if (excludeBuyerOrSellerIds is { Count: > 0 })
            {
                var excl = excludeBuyerOrSellerIds;
                q = q.Where(t => !excl.Contains(t.BuyerId) && !excl.Contains(t.SellerId));
            }
            if (userIdFilter.HasValue)
            {
                var uid = userIdFilter.Value;
                q = q.Where(t => t.BuyerId == uid || t.SellerId == uid);
            }
            if (stockIdFilter.HasValue)
            {
                var sid = stockIdFilter.Value;
                q = q.Where(t => t.StockId == sid);
            }
            if (!string.IsNullOrWhiteSpace(currencyFilter))
            {
                var cf = currencyFilter.ToUpperInvariant();
                q = q.Where(t => t.Currency == cf);
            }
            var total = await q.CountAsync();
            var ordered = (sortKey, desc) switch
            {
                ("TransactionId", true)  => q.OrderByDescending(t => t.TransactionId),
                ("TransactionId", false) => q.OrderBy(t => t.TransactionId),
                ("StockId",       true)  => q.OrderByDescending(t => t.StockId),
                ("StockId",       false) => q.OrderBy(t => t.StockId),
                ("Quantity",      true)  => q.OrderByDescending(t => t.Quantity),
                ("Quantity",      false) => q.OrderBy(t => t.Quantity),
                ("Price",         true)  => q.OrderByDescending(t => t.Price),
                ("Price",         false) => q.OrderBy(t => t.Price),
                // Total / BuyerName / SellerName: sorted in-VM after the page is built.
                (_,               true)  => q.OrderByDescending(t => t.Timestamp),
                (_,               false) => q.OrderBy(t => t.Timestamp),
            };
            var rows = await ordered.Skip(skip).Take(take).ToListAsync();
            return (rows.Select(TransactionMapper.ToDomain).ToList(), total);
        }, ct);
    }

    public async Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<TransactionRow>()
               .Where(t => t.TransactionId == transactionId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : TransactionMapper.ToDomain(row);
    }

    public async Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<TransactionRow>()
               .Where(t => t.BuyerId == userId || t.SellerId == userId)
               .ToListAsync(),
            ct);
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsByOrderId(int orderId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<TransactionRow>()
               .Where(t => t.BuyOrderId == orderId || t.SellOrderId == orderId)
               .OrderBy(t => t.Timestamp)
               .ToListAsync(),
            ct);
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(int stockId, CurrencyType currency,
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var rows = await RunDbAsync(() =>
            _db.Table<TransactionRow>()
               .Where(t => t.StockId == stockId && t.Timestamp >= from && t.Timestamp < to && t.Currency == currencyCode )
               .ToListAsync(),
            ct);
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var now = TimeHelper.NowUtc();
        var rows = await RunDbAsync(() =>
            _db.Table<TransactionRow>()
               .Where(t => t.Timestamp >= since && t.Timestamp <= now)
               .ToListAsync(), ct);
        return rows.Select(TransactionMapper.ToDomain).ToList();
    }

    public async Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var row = await RunDbAsync(() =>
            _db.Table<TransactionRow>()
               .Where(t => t.StockId == stockId && t.Currency == currencyCode)
               .OrderByDescending(t => t.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : TransactionMapper.ToDomain(row);
    }

    public async Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency,
        DateTime time, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var row = await RunDbAsync(() =>
            _db.Table<TransactionRow>()
               .Where(t => t.StockId == stockId && t.Currency == currencyCode && t.Timestamp <= time)
               .OrderByDescending(t => t.Timestamp)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : TransactionMapper.ToDomain(row);
    }

    public async Task CreateTransaction(Transaction transaction, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!transaction.IsValid())
            throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        var row = TransactionMapper.ToRow(transaction);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        transaction.TransactionId = row.TransactionId;
    }

    public async Task UpdateTransaction(Transaction transaction, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!transaction.IsValid())
            throw new ArgumentException("Transaction entity is not valid", nameof(transaction));
        await RunDbAsync(() => _db.UpdateAsync(TransactionMapper.ToRow(transaction)), ct);
    }

    public async Task DeleteTransaction(Transaction transaction, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (transaction.TransactionId == 0)
            throw new ArgumentException("Transaction entity must have a valid TransactionId", nameof(transaction));
        await RunDbAsync(() => _db.DeleteAsync(TransactionMapper.ToRow(transaction)), ct);
    }
    #endregion

    #region Position operations
    public async Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<PositionRow>().ToListAsync(), ct);
        return rows.Select(PositionMapper.ToDomain).ToList();
    }

    public async Task<(List<Position> Items, int Total)> GetPositionsPageAsync(int stockId, int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(async () =>
        {
            var q = _db.Table<PositionRow>().Where(p => p.StockId == stockId);
            if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var userId))
                q = q.Where(p => p.UserId == userId);
            var total = await q.CountAsync();
            var ordered = (sortKey, desc) switch
            {
                ("UserId",   true)  => q.OrderByDescending(p => p.UserId),
                ("UserId",   false) => q.OrderBy(p => p.UserId),
                ("Quantity", true)  => q.OrderByDescending(p => p.Quantity),
                ("Quantity", false) => q.OrderBy(p => p.Quantity),
                ("Reserved", true)  => q.OrderByDescending(p => p.ReservedQuantity),
                ("Reserved", false) => q.OrderBy(p => p.ReservedQuantity),
                (_,          true)  => q.OrderByDescending(p => p.UserId),
                (_,          false) => q.OrderBy(p => p.UserId),
            };
            var rows = await ordered.Skip(skip).Take(take).ToListAsync();
            return (rows.Select(PositionMapper.ToDomain).ToList(), total);
        }, ct);
    }

    public async Task<Position?> GetPositionById(int positionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<PositionRow>()
               .Where(p => p.PositionId == positionId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : PositionMapper.ToDomain(row);
    }

    public async Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<PositionRow>()
               .Where(p => p.UserId == userId)
               .ToListAsync(),
            ct);
        return rows.Select(PositionMapper.ToDomain).ToList();
    }

    public async Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<PositionRow>()
               .Where(p => p.UserId == userId && p.StockId == stockId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : PositionMapper.ToDomain(row);
    }

    public async Task<List<Position>> GetPositionsForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<Position>();
        await InitializeAsync(ct);
        var rows = await ChunkedContainsAsync(userIds, chunk => RunDbAsync(() =>
            _db.Table<PositionRow>().Where(p => chunk.Contains(p.UserId)).ToListAsync(), ct), ct);
        return rows.Select(PositionMapper.ToDomain).ToList();
    }

    public async Task CreatePosition(Position position, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));
        var row = PositionMapper.ToRow(position);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        position.PositionId = row.PositionId;
    }

    public async Task UpdatePosition(Position position, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!position.IsValid())
            throw new ArgumentException("Position entity is not valid", nameof(position));
        await RunDbAsync(() => _db.UpdateAsync(PositionMapper.ToRow(position)), ct);
    }

    public async Task DeletePosition(Position position, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (position.PositionId == 0)
            throw new ArgumentException("Position entity must have a valid PositionId", nameof(position));
        await RunDbAsync(() => _db.DeleteAsync(PositionMapper.ToRow(position)), ct);
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
            await RunDbAsync(() => _db.UpdateAsync(PositionMapper.ToRow(position)), ct);
        }
        else
        {
            var row = PositionMapper.ToRow(position);
            await RunDbAsync(() => _db.InsertAsync(row), ct);
            position.PositionId = row.PositionId;
        }
    }
    #endregion

    #region Fund operations
    public async Task<List<Fund>> GetFundsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<FundRow>().ToListAsync(), ct);
        return rows.Select(FundMapper.ToDomain).ToList();
    }

    // Returns paged user IDs for the Fund table. sortKey is "UserId" or a currency code ("USD", "EUR", …).
    public async Task<(List<int> UserIds, int Total)> GetFundsUserIdsPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(async () =>
        {
            var knownCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "USD", "EUR", "GBP", "JPY", "CHF", "AUD" };

            if (knownCurrencies.Contains(sortKey))
            {
                // Sort by TotalBalance for the given currency
                string code = sortKey.ToUpperInvariant();
                var q = _db.Table<FundRow>().Where(f => f.Currency == code);
                if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var filterId))
                    q = q.Where(f => f.UserId == filterId);
                var total = await q.CountAsync();
                var ordered = desc ? q.OrderByDescending(f => f.TotalBalance) : q.OrderBy(f => f.TotalBalance);
                var funds = await ordered.Skip(skip).Take(take).ToListAsync();
                return (funds.Select(f => f.UserId).ToList(), total);
            }
            else if (sortKey == "Reserved")
            {
                // Sort by ReservedBalance of USD fund
                var q = _db.Table<FundRow>().Where(f => f.Currency == "USD");
                if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var filterId))
                    q = q.Where(f => f.UserId == filterId);
                var total = await q.CountAsync();
                var ordered = desc ? q.OrderByDescending(f => f.ReservedBalance) : q.OrderBy(f => f.ReservedBalance);
                var funds = await ordered.Skip(skip).Take(take).ToListAsync();
                return (funds.Select(f => f.UserId).ToList(), total);
            }
            else
            {
                // Sort by UserId — query Users table
                var q = _db.Table<UserRow>();
                if (!string.IsNullOrWhiteSpace(filter) && int.TryParse(filter.Trim(), out var filterId))
                    q = q.Where(u => u.UserId == filterId);
                var total = await q.CountAsync();
                var ordered = desc ? q.OrderByDescending(u => u.UserId) : q.OrderBy(u => u.UserId);
                var users = await ordered.Skip(skip).Take(take).ToListAsync();
                return (users.Select(u => u.UserId).ToList(), total);
            }
        }, ct);
    }

    // Long-form Funds page: one row per (UserId, Currency).
    public async Task<(List<Fund> Items, int Total)> GetFundsPageAsync(int skip, int take, string sortKey, bool desc,
        int? userIdFilter = null, bool hasNonZero = false, bool hasReserved = false, string? currencyFilter = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(async () =>
        {
            var q = _db.Table<FundRow>();
            if (userIdFilter.HasValue)
            {
                var uid = userIdFilter.Value;
                q = q.Where(f => f.UserId == uid);
            }
            if (hasNonZero)    q = q.Where(f => f.TotalBalance > 0m);
            if (hasReserved)   q = q.Where(f => f.ReservedBalance > 0m);
            if (!string.IsNullOrWhiteSpace(currencyFilter))
            {
                var cf = currencyFilter.ToUpperInvariant();
                q = q.Where(f => f.Currency == cf);
            }
            var total = await q.CountAsync();
            var ordered = (sortKey, desc) switch
            {
                ("UserId",          true)  => q.OrderByDescending(f => f.UserId),
                ("UserId",          false) => q.OrderBy(f => f.UserId),
                ("TotalBalance",    true)  => q.OrderByDescending(f => f.TotalBalance),
                ("TotalBalance",    false) => q.OrderBy(f => f.TotalBalance),
                ("ReservedBalance", true)  => q.OrderByDescending(f => f.ReservedBalance),
                ("ReservedBalance", false) => q.OrderBy(f => f.ReservedBalance),
                ("Currency",        true)  => q.OrderByDescending(f => f.Currency),
                ("Currency",        false) => q.OrderBy(f => f.Currency),
                ("UpdatedAt",       true)  => q.OrderByDescending(f => f.UpdatedAt),
                ("UpdatedAt",       false) => q.OrderBy(f => f.UpdatedAt),
                (_,                 true)  => q.OrderByDescending(f => f.UserId),
                (_,                 false) => q.OrderBy(f => f.UserId),
            };
            var rows = await ordered.Skip(skip).Take(take).ToListAsync();
            return (rows.Select(FundMapper.ToDomain).ToList(), total);
        }, ct);
    }

    public async Task<Fund?> GetFundById(int fundId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<FundRow>()
               .Where(f => f.FundId == fundId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : FundMapper.ToDomain(row);
    }

    public async Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<FundRow>()
               .Where(f => f.UserId == userId)
               .ToListAsync(),
            ct);
        return rows.Select(FundMapper.ToDomain).ToList();
    }

    public async Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var row = await RunDbAsync(() =>
            _db.Table<FundRow>()
               .Where(f => f.UserId == userId && f.Currency == currencyCode)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : FundMapper.ToDomain(row);
    }

    public async Task<List<Fund>> GetFundsForUsersAsync(List<int> userIds, CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0) return new List<Fund>();
        await InitializeAsync(ct);
        var rows = await ChunkedContainsAsync(userIds, chunk => RunDbAsync(() =>
            _db.Table<FundRow>().Where(f => chunk.Contains(f.UserId)).ToListAsync(), ct), ct);
        return rows.Select(FundMapper.ToDomain).ToList();
    }

    public async Task CreateFund(Fund fund, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));
        var row = FundMapper.ToRow(fund);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        fund.FundId = row.FundId;
    }

    public async Task UpdateFund(Fund fund, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!fund.IsValid())
            throw new ArgumentException("Fund entity is not valid", nameof(fund));
        await RunDbAsync(() => _db.UpdateAsync(FundMapper.ToRow(fund)), ct);
    }

    public async Task DeleteFund(Fund fund, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (fund.FundId == 0)
            throw new ArgumentException("Fund entity must have a valid FundId", nameof(fund));
        await RunDbAsync(() => _db.DeleteAsync(FundMapper.ToRow(fund)), ct);
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
            await RunDbAsync(() => _db.UpdateAsync(FundMapper.ToRow(fund)), ct);
        }
        else
        {
            var row = FundMapper.ToRow(fund);
            await RunDbAsync(() => _db.InsertAsync(row), ct);
            fund.FundId = row.FundId;
        }
    }
    #endregion

    #region Candle operations
    public async Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<CandleRow>().ToListAsync(), ct);
        return rows.Select(CandleMapper.ToDomain).ToList();
    }

    public async Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<CandleRow>()
               .Where(c => c.CandleId == candleId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : CandleMapper.ToDomain(row);
    }

    public async Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var rows = await RunDbAsync(() =>
            _db.Table<CandleRow>()
               .Where(c => c.StockId == stockId && c.Currency == currencyCode)
               .ToListAsync(),
            ct);
        return rows.Select(CandleMapper.ToDomain).ToList();
    }

    public async Task<List<Candle>> GetCandlesByStockIdAndTimeRange(int stockId, CurrencyType currency,
        TimeSpan resolution, DateTime from, DateTime to, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var currencyCode = currency.ToString();
        var resolutionSeconds = (int)resolution.TotalSeconds;
        var rows = await RunDbAsync(() =>
            _db.Table<CandleRow>()
               .Where(c => c.StockId == stockId && c.Currency == currencyCode && c.BucketSeconds == resolutionSeconds
                        && c.OpenTime >= from && c.OpenTime < to)
               .OrderByDescending(c => c.OpenTime)
               .ToListAsync(),
            ct);
        return rows.Select(CandleMapper.ToDomain).ToList();
    }

    public async Task CreateCandle(Candle candle, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));
        var row = CandleMapper.ToRow(candle);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        candle.CandleId = row.CandleId;
    }

    public async Task UpdateCandle(Candle candle, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!candle.IsValid())
            throw new ArgumentException("Candle entity is not valid", nameof(candle));
        await RunDbAsync(() => _db.UpdateAsync(CandleMapper.ToRow(candle)), ct);
    }

    public async Task DeleteCandle(Candle candle, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (candle.CandleId == 0)
            throw new ArgumentException("Candle entity must have a valid CandleId", nameof(candle));
        await RunDbAsync(() => _db.DeleteAsync(CandleMapper.ToRow(candle)), ct);
    }

    public async Task UpsertCandle(Candle candle, CancellationToken ct = default)
    {
        if (candle is null) throw new ArgumentNullException(nameof(candle));
        await UpsertCandlesAsync(new[] { candle }, ct).ConfigureAwait(false);
    }

    // SQL is built once. The conflict target is the (StockId, Currency, BucketSeconds, OpenTime)
    // unique index — declared on the model and ensured idempotently in InitializeAsync.
    private const string UpsertCandleSql =
        "INSERT INTO Candles (StockId, Currency, BucketSeconds, OpenTime, " +
        "Open, High, Low, Close, Volume, TradeCount, MinTransactionId, MaxTransactionId) " +
        "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) " +
        "ON CONFLICT(StockId, Currency, BucketSeconds, OpenTime) DO UPDATE SET " +
        "Open = excluded.Open, High = excluded.High, Low = excluded.Low, Close = excluded.Close, " +
        "Volume = excluded.Volume, TradeCount = excluded.TradeCount, " +
        "MinTransactionId = excluded.MinTransactionId, MaxTransactionId = excluded.MaxTransactionId;";

    public async Task UpsertCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
    {
        if (candles is null || candles.Count == 0) return;
        await InitializeAsync(ct);

        for (int i = 0; i < candles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candles[i];
            if (!c.IsValid())
                throw new ArgumentException(
                    $"Candle entity is not valid (StockId={c.StockId}, OpenTime={c.OpenTime:o}).",
                    nameof(candles));

            // Capture locals so the closure doesn't re-read the indexer on Task.Run thread.
            var stockId = c.StockId;
            var currency = c.Currency;
            var bucket = c.BucketSeconds;
            var openTime = c.OpenTime;
            var open = c.Open;
            var high = c.High;
            var low = c.Low;
            var close = c.Close;
            var volume = c.Volume;
            var tradeCount = c.TradeCount;
            var minTx = c.MinTransactionId;
            var maxTx = c.MaxTransactionId;

            await RunDbAsync(() => _db.ExecuteAsync(UpsertCandleSql,
                stockId, currency, bucket, openTime,
                open, high, low, close,
                volume, tradeCount,
                minTx, maxTx), ct).ConfigureAwait(false);
        }
    }
    #endregion

    #region Message operations
    public async Task<List<Message>> GetMessagesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<MessageRow>()
               .OrderByDescending(m => m.CreatedAt)
               .ThenByDescending(m => m.MessageId)
               .ToListAsync(), ct);
        return rows.Select(MessageMapper.ToDomain).ToList();
    }

    public async Task<Message?> GetMessageById(int messageId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<MessageRow>().Where(m => m.MessageId == messageId).FirstOrDefaultAsync(), ct);
        return row is null ? null : MessageMapper.ToDomain(row);
    }

    public async Task<List<Message>> GetMessagesByUserId(
        int userId, bool onlyUnread = false, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
        {
            var q = _db.Table<MessageRow>().Where(m => m.UserId == userId);
            if (onlyUnread) q = q.Where(m => m.ReadAt == null);
            q = q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.MessageId);
            return q.ToListAsync();
        }, ct);
        return rows.Select(MessageMapper.ToDomain).ToList();
    }

    public async Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        return await RunDbAsync(() =>
            _db.Table<MessageRow>()
               .Where(m => m.UserId == userId && m.ReadAt == null)
               .CountAsync(), ct);
    }

    public async Task CreateMessage(Message message, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!message.IsValid())
            throw new ArgumentException("Message entity is not valid", nameof(message));
        var row = MessageMapper.ToRow(message);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        message.MessageId = row.MessageId;
    }

    public async Task UpdateMessage(Message message, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!message.IsValid())
            throw new ArgumentException("Message entity is not valid", nameof(message));
        await RunDbAsync(() => _db.UpdateAsync(MessageMapper.ToRow(message)), ct);
    }

    public async Task DeleteMessage(Message message, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (message.MessageId == 0)
            throw new ArgumentException("Message entity must have a valid MessageId", nameof(message));
        await RunDbAsync(() => _db.DeleteAsync(MessageMapper.ToRow(message)), ct);
    }

    public async Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var msg = await GetMessageById(messageId, ct);
        if (msg is null) return false;
        if (!msg.IsRead)
        {
            msg.MarkAsRead();
            await RunDbAsync(() => _db.UpdateAsync(MessageMapper.ToRow(msg)), ct);
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

    #region FundTransaction operations
    public async Task<List<FundTransaction>> GetFundTransactionsByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<FundTransactionRow>()
               .Where(t => t.UserId == userId)
               .OrderByDescending(t => t.CreatedAt)
               .ToListAsync(),
            ct);
        return rows.Select(FundTransactionMapper.ToDomain).ToList();
    }

    public async Task CreateFundTransaction(FundTransaction tx, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!tx.IsValid())
            throw new ArgumentException("FundTransaction entity is not valid", nameof(tx));
        var row = FundTransactionMapper.ToRow(tx);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        tx.FundTransactionId = row.FundTransactionId;
    }
    #endregion

    #region UserPreferences operations
    public async Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<UserPreferencesRow>()
               .Where(p => p.UserId == userId)
               .FirstOrDefaultAsync(),
            ct);
        return row is null ? null : UserPreferencesMapper.ToDomain(row);
    }

    public async Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!prefs.IsValid())
            throw new ArgumentException("UserPreferences entity is not valid", nameof(prefs));

        // PK is UserId (no AutoIncrement). InsertOrReplaceAsync handles both cases atomically.
        var row = UserPreferencesMapper.ToRow(prefs);
        await RunDbAsync(() => _db.InsertOrReplaceAsync(row), ct);
    }
    #endregion

    #region UserWatchlist operations
    public async Task<List<UserWatchlistEntry>> GetWatchlistByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<UserWatchlistEntryRow>()
               .Where(w => w.UserId == userId)
               .OrderBy(w => w.SortOrder)
               .ToListAsync(),
            ct);
        return rows.Select(UserWatchlistEntryMapper.ToDomain).ToList();
    }

    public async Task UpsertWatchlistEntry(UserWatchlistEntry entry, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!entry.IsValid())
            throw new ArgumentException("UserWatchlistEntry is not valid", nameof(entry));

        // Id is AutoIncrement: Id=0 inserts new (composite unique index on (UserId, StockId)
        // protects against duplicates), Id>0 updates that row (used for SortOrder edits).
        var row = UserWatchlistEntryMapper.ToRow(entry);
        await RunDbAsync(() => _db.InsertOrReplaceAsync(row), ct);
        entry.Id = row.Id;
    }

    public async Task<bool> DeleteWatchlistEntry(int userId, int stockId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.ExecuteAsync(
            "DELETE FROM UserWatchlist WHERE UserId = ? AND StockId = ?", userId, stockId), ct);
        return rows > 0;
    }

    public async Task ReplaceWatchlistAsync(int userId, IReadOnlyList<UserWatchlistEntry> entries, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        await RunInTransactionAsync(async _ =>
        {
            await _db.ExecuteAsync("DELETE FROM UserWatchlist WHERE UserId = ?", userId);
            foreach (var e in entries)
            {
                if (e.UserId != userId)
                    throw new ArgumentException($"Entry UserId {e.UserId} does not match caller {userId}.", nameof(entries));
                if (!e.IsValid())
                    throw new ArgumentException("UserWatchlistEntry is not valid.", nameof(entries));
                // Force a fresh Id so the unique index isn't tripped by a stale value.
                var insert = new UserWatchlistEntryRow
                {
                    UserId = e.UserId,
                    StockId = e.StockId,
                    SortOrder = e.SortOrder,
                    AddedAt = e.AddedAt
                };
                await _db.InsertAsync(insert);
            }
        }, ct);
    }
    #endregion

    #region AIUser operations
    public async Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() => _db.Table<AIUserRow>().ToListAsync(), ct);
        return rows.Select(AIUserMapper.ToDomain).ToList();
    }

    public async Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var row = await RunDbAsync(() =>
            _db.Table<AIUserRow>().Where(a => a.AiUserId == aiUserId).FirstOrDefaultAsync(), ct);
        return row is null ? null : AIUserMapper.ToDomain(row);
    }

    public async Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var rows = await RunDbAsync(() =>
            _db.Table<AIUserRow>().Where(a => a.UserId == userId).ToListAsync(), ct);
        return rows.Select(AIUserMapper.ToDomain).ToList();
    }

    public async Task CreateAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!aiUser.IsValid())
            throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));
        var row = AIUserMapper.ToRow(aiUser);
        await RunDbAsync(() => _db.InsertAsync(row), ct);
        aiUser.AiUserId = row.AiUserId;
    }

    public async Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (!aiUser.IsValid())
            throw new ArgumentException("AIUser entity is not valid", nameof(aiUser));
        await RunDbAsync(() => _db.UpdateAsync(AIUserMapper.ToRow(aiUser)), ct);
    }

    public async Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (aiUser.AiUserId == 0)
            throw new ArgumentException("AIUser entity must have a valid AiUserId", nameof(aiUser));
        await RunDbAsync(() => _db.DeleteAsync(AIUserMapper.ToRow(aiUser)), ct);
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
            await RunDbAsync(() => _db.UpdateAsync(AIUserMapper.ToRow(aiUser)), ct);
        }
        else
        {
            var row = AIUserMapper.ToRow(aiUser);
            await RunDbAsync(() => _db.InsertAsync(row), ct);
            aiUser.AiUserId = row.AiUserId;
        }
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

            await _db.CreateTableAsync<UserRow>();
            await _db.CreateTableAsync<StockRow>();
            await _db.CreateTableAsync<StockListingRow>();
            await _db.CreateTableAsync<StockPriceRow>();
            await _db.CreateTableAsync<OrderRow>();
            await _db.CreateTableAsync<TransactionRow>();
            await _db.CreateTableAsync<PositionRow>();
            await _db.CreateTableAsync<FundRow>();
            await _db.CreateTableAsync<FundTransactionRow>();
            await _db.CreateTableAsync<UserPreferencesRow>();
            await _db.CreateTableAsync<UserWatchlistEntryRow>();
            await _db.CreateTableAsync<CandleRow>();
            await _db.CreateTableAsync<MessageRow>();
            await _db.CreateTableAsync<AIUserRow>();

            // Ensure the composite unique index exists on older DBs so the candle ON CONFLICT
            // upsert path has a target. CreateTableAsync handles this on fresh DBs via the
            // [Indexed(Unique=true)] attributes, but pre-existing data files may pre-date them.
            await _db.ExecuteAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_Candle_Key ON Candles(StockId, Currency, BucketSeconds, OpenTime);");

            // SQLite-net-pcl doesn't emit CHECK constraints, so the Fund/Position invariants
            // (TotalBalance >= ReservedBalance, Quantity >= ReservedQuantity, no negatives)
            // are only enforced at the C# model layer. Triggers give a DB-level safety net
            // that catches anything bypassing the model (raw SQL, future tooling, etc.).
            await CreateInvariantTriggers();

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

    private async Task CreateInvariantTriggers()
    {
        // Funds: TotalBalance >= ReservedBalance, neither negative.
        await _db.ExecuteAsync(@"
            CREATE TRIGGER IF NOT EXISTS trg_funds_invariant_insert
            BEFORE INSERT ON Funds
            WHEN NEW.TotalBalance < 0 OR NEW.ReservedBalance < 0
                 OR NEW.ReservedBalance > NEW.TotalBalance
            BEGIN SELECT RAISE(ABORT, 'Fund invariant violated'); END;");
        await _db.ExecuteAsync(@"
            CREATE TRIGGER IF NOT EXISTS trg_funds_invariant_update
            BEFORE UPDATE ON Funds
            WHEN NEW.TotalBalance < 0 OR NEW.ReservedBalance < 0
                 OR NEW.ReservedBalance > NEW.TotalBalance
            BEGIN SELECT RAISE(ABORT, 'Fund invariant violated'); END;");

        // Positions: Quantity >= ReservedQuantity, neither negative.
        await _db.ExecuteAsync(@"
            CREATE TRIGGER IF NOT EXISTS trg_positions_invariant_insert
            BEFORE INSERT ON Positions
            WHEN NEW.Quantity < 0 OR NEW.ReservedQuantity < 0
                 OR NEW.ReservedQuantity > NEW.Quantity
            BEGIN SELECT RAISE(ABORT, 'Position invariant violated'); END;");
        await _db.ExecuteAsync(@"
            CREATE TRIGGER IF NOT EXISTS trg_positions_invariant_update
            BEFORE UPDATE ON Positions
            WHEN NEW.Quantity < 0 OR NEW.ReservedQuantity < 0
                 OR NEW.ReservedQuantity > NEW.Quantity
            BEGIN SELECT RAISE(ABORT, 'Position invariant violated'); END;");
    }

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
