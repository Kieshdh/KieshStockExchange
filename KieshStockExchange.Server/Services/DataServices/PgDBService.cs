using Dapper;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Server.Data;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// Postgres + Dapper implementation of <see cref="IDataBaseService"/>. Lives
/// alongside <see cref="DBService"/> and is selected by the `Db:Backend` config
/// flag in <c>Program.cs</c>. Per-region methods are split into partial files
/// so each rewrite commit touches one file.
/// </summary>
public sealed partial class PgDBService : IDataBaseService
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<PgDBService> _logger;

    // Ambient connection + transaction inherited across awaits. Set by Begin/Run,
    // cleared on root Commit/Rollback. Nested transactions reuse the same scope
    // and chain via SAVEPOINT.
    private static readonly AsyncLocal<TxScope?> _ambient = new();

    public PgDBService(IDbConnectionFactory factory, ILogger<PgDBService> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private async ValueTask<DbScope> OpenAsync(CancellationToken ct)
    {
        if (_ambient.Value is { } a)
            return new DbScope(a.Connection, a.Transaction, ownsConnection: false);
        var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        return new DbScope(conn, transaction: null, ownsConnection: true);
    }

    #region Generic operations
    public async Task ResetTableAsync<T>(CancellationToken ct = default) where T : new()
    {
        var table = ResolveTableName<T>();
        await using var c = await OpenAsync(ct);
        await c.ExecuteAsync($@"TRUNCATE TABLE ""{table}"" RESTART IDENTITY CASCADE");
    }

    // Hot types (Order/Transaction/Position/Fund/FundTransaction) get a
    // single multi-row VALUES statement when N>1 so a bot trade group's
    // ~20 round-trips collapses to ~5. Cold types and N==1 fall through to
    // the per-row loop below — batch SQL has measurable overhead at N=1
    // and 14 settlement sites pass single-element arrays.
    public async Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
    {
        var list = items as IReadOnlyList<T> ?? items.ToList();
        if (list.Count == 0) return;

        if (list.Count > 1)
        {
            switch (list)
            {
                case IReadOnlyList<Order> orders:
                    await InsertOrdersBatchAsync(orders, ct).ConfigureAwait(false);
                    return;
                case IReadOnlyList<Transaction> txs:
                    await InsertTransactionsBatchAsync(txs, ct).ConfigureAwait(false);
                    return;
                case IReadOnlyList<Position> positions:
                    await InsertPositionsBatchAsync(positions, ct).ConfigureAwait(false);
                    return;
                case IReadOnlyList<FundTransaction> fts:
                    await InsertFundTransactionsBatchAsync(fts, ct).ConfigureAwait(false);
                    return;
            }
        }

        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case User u:               await CreateUser(u, ct); break;
                case Stock s:              await CreateStock(s, ct); break;
                case StockListing l:       await CreateStockListing(l, ct); break;
                case StockPrice sp:        await CreateStockPrice(sp, ct); break;
                case Order o:              await CreateOrder(o, ct); break;
                case Transaction t:        await CreateTransaction(t, ct); break;
                case Position p:           await CreatePosition(p, ct); break;
                case Fund f:               await CreateFund(f, ct); break;
                case FundTransaction ft:   await CreateFundTransaction(ft, ct); break;
                case Candle cd:            await CreateCandle(cd, ct); break;
                case Message m:            await CreateMessage(m, ct); break;
                case UserWatchlistEntry w: await UpsertWatchlistEntry(w, ct); break;
                case AIUser a:             await CreateAIUser(a, ct); break;
                case UserPreferences up:   await UpsertUserPreferences(up, ct); break;
                default:
                    throw new NotSupportedException($"InsertAllAsync<{typeof(T).Name}> is not supported.");
            }
        }
    }

    public async Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
    {
        var list = items as IReadOnlyList<T> ?? items.ToList();
        if (list.Count == 0) return;

        if (list.Count > 1)
        {
            switch (list)
            {
                case IReadOnlyList<Order> orders:
                    await UpdateOrdersBatchAsync(orders, ct).ConfigureAwait(false);
                    return;
                case IReadOnlyList<Fund> funds:
                    await UpdateFundsBatchAsync(funds, ct).ConfigureAwait(false);
                    return;
                case IReadOnlyList<Position> positions:
                    await UpdatePositionsBatchAsync(positions, ct).ConfigureAwait(false);
                    return;
            }
        }

        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case User u:               await UpdateUser(u, ct); break;
                case Stock s:              await UpdateStock(s, ct); break;
                case StockPrice sp:        await UpdateStockPrice(sp, ct); break;
                case Order o:              await UpdateOrder(o, ct); break;
                case Transaction t:        await UpdateTransaction(t, ct); break;
                case Position p:           await UpdatePosition(p, ct); break;
                case Fund f:               await UpdateFund(f, ct); break;
                case Candle cd:            await UpdateCandle(cd, ct); break;
                case Message m:            await UpdateMessage(m, ct); break;
                case AIUser a:             await UpdateAIUser(a, ct); break;
                case UserPreferences up:   await UpsertUserPreferences(up, ct); break;
                default:
                    throw new NotSupportedException($"UpdateAllAsync<{typeof(T).Name}> is not supported.");
            }
        }
    }

    public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_ambient.Value is { } ambient)
        {
            // Nested → SAVEPOINT on the existing connection/transaction.
            var spName = "sp_" + Guid.NewGuid().ToString("N");
            await ambient.Connection.ExecuteAsync(
                $"SAVEPOINT {spName}", transaction: ambient.Transaction);
            return new PgTransaction(ambient.Connection, ambient.Transaction, isRoot: false, savepoint: spName);
        }

        // Root → fresh conn + tx, install as ambient. Released on Commit/Rollback.
        var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        NpgsqlTransaction tx;
        try
        {
            tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _ambient.Value = new TxScope(conn, tx);
        return new PgTransaction(conn, tx, isRoot: true, savepoint: null);
    }

    public async Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        await using var tx = (PgTransaction)await BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await action(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // The schema-reset path. Migration history table goes with the schema, so the
    // caller must run `dotnet ef database update` (or equivalent) to repopulate the
    // tables after this returns.
    public async Task DropAndRecreateAsync(bool keepBackup = false, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync(@"DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        _logger.LogWarning(
            "Postgres schema dropped. Run `dotnet ef database update` to re-apply migrations.");
    }
    #endregion

    // ----- Internal types ------------------------------------------------

    /// <summary>
    /// Ambient connection + transaction. Held in an AsyncLocal so calls
    /// originating from any async context inside RunInTransactionAsync share
    /// the same physical connection.
    /// </summary>
    private sealed record TxScope(NpgsqlConnection Connection, NpgsqlTransaction Transaction);

    /// <summary>
    /// Connection wrapper passed to region methods. Routes Dapper calls through
    /// the ambient transaction when present, and only disposes the connection
    /// it actually owns.
    /// </summary>
    internal readonly struct DbScope
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction? _tx;
        private readonly bool _own;

        public DbScope(NpgsqlConnection conn, NpgsqlTransaction? transaction, bool ownsConnection)
        {
            _conn = conn;
            _tx = transaction;
            _own = ownsConnection;
        }

        public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null) =>
            _conn.QueryAsync<T>(sql, param, _tx);

        public Task<T> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) =>
            _conn.QuerySingleOrDefaultAsync<T>(sql, param, _tx);

        public Task<T> ExecuteScalarAsync<T>(string sql, object? param = null) =>
            _conn.ExecuteScalarAsync<T>(sql, param, _tx);

        public Task<int> ExecuteAsync(string sql, object? param = null) =>
            _conn.ExecuteAsync(sql, param, _tx);

        public ValueTask DisposeAsync() =>
            _own ? _conn.DisposeAsync() : ValueTask.CompletedTask;
    }

    /// <summary>
    /// ITransaction implementation. Each instance captures its own conn+tx
    /// references so Commit/Rollback don't depend on AsyncLocal still being
    /// in scope at the time they run.
    /// </summary>
    private sealed class PgTransaction : ITransaction
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction _tx;
        private readonly string? _savepoint;
        private bool _completed;

        public bool IsRoot { get; }

        public PgTransaction(NpgsqlConnection conn, NpgsqlTransaction tx, bool isRoot, string? savepoint)
        {
            _conn = conn;
            _tx = tx;
            IsRoot = isRoot;
            _savepoint = savepoint;
        }

        public async ValueTask CommitAsync(CancellationToken ct = default)
        {
            if (_completed) return;
            _completed = true;

            if (IsRoot)
            {
                try
                {
                    await _tx.CommitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await DisposeRootAsync().ConfigureAwait(false);
                }
            }
            else
            {
                await _conn.ExecuteAsync(
                    $"RELEASE SAVEPOINT {_savepoint}", transaction: _tx)
                    .ConfigureAwait(false);
            }
        }

        public async ValueTask RollbackAsync(CancellationToken ct = default)
        {
            if (_completed) return;
            _completed = true;

            if (IsRoot)
            {
                try
                {
                    await _tx.RollbackAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await DisposeRootAsync().ConfigureAwait(false);
                }
            }
            else
            {
                await _conn.ExecuteAsync(
                    $"ROLLBACK TO SAVEPOINT {_savepoint}", transaction: _tx)
                    .ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed) await RollbackAsync().ConfigureAwait(false);
        }

        private async ValueTask DisposeRootAsync()
        {
            await _tx.DisposeAsync().ConfigureAwait(false);
            await _conn.DisposeAsync().ConfigureAwait(false);
            _ambient.Value = null;
        }
    }

    private static string ResolveTableName<T>()
    {
        var t = typeof(T);
        if (t == typeof(User))               return "Users";
        if (t == typeof(Stock))              return "Stocks";
        if (t == typeof(StockListing))       return "StockListings";
        if (t == typeof(StockPrice))         return "StockPrices";
        if (t == typeof(Order))              return "Orders";
        if (t == typeof(Transaction))        return "Transactions";
        if (t == typeof(Position))           return "Positions";
        if (t == typeof(Fund))               return "Funds";
        if (t == typeof(FundTransaction))    return "FundTransactions";
        if (t == typeof(Candle))             return "Candles";
        if (t == typeof(Message))            return "Messages";
        if (t == typeof(UserPreferences))    return "UserPreferences";
        if (t == typeof(UserWatchlistEntry)) return "UserWatchlist";
        if (t == typeof(AIUser))             return "AIUsers";
        throw new NotSupportedException($"ResetTableAsync<{t.Name}> is not supported.");
    }
}
