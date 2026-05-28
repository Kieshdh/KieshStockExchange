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

    public async Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
    {
        foreach (var item in items)
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
        foreach (var item in items)
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
            return new PgTransaction(this, isRoot: false, savepoint: spName);
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
        return new PgTransaction(this, isRoot: true, savepoint: null);
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
    /// ITransaction implementation. Root commit/rollback closes the underlying
    /// NpgsqlTransaction; nested ones release or rollback a savepoint.
    /// </summary>
    private sealed class PgTransaction : ITransaction
    {
        private readonly PgDBService _owner;
        private readonly string? _savepoint;
        private bool _completed;

        public bool IsRoot { get; }

        public PgTransaction(PgDBService owner, bool isRoot, string? savepoint)
        {
            _owner = owner;
            IsRoot = isRoot;
            _savepoint = savepoint;
        }

        public async ValueTask CommitAsync(CancellationToken ct = default)
        {
            if (_completed) return;
            _completed = true;

            var scope = _ambient.Value
                ?? throw new InvalidOperationException("Transaction scope missing.");

            if (IsRoot)
            {
                try
                {
                    await scope.Transaction.CommitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await DisposeRootScopeAsync(scope).ConfigureAwait(false);
                }
            }
            else
            {
                await scope.Connection.ExecuteAsync(
                    $"RELEASE SAVEPOINT {_savepoint}", transaction: scope.Transaction)
                    .ConfigureAwait(false);
            }
        }

        public async ValueTask RollbackAsync(CancellationToken ct = default)
        {
            if (_completed) return;
            _completed = true;

            var scope = _ambient.Value
                ?? throw new InvalidOperationException("Transaction scope missing.");

            if (IsRoot)
            {
                try
                {
                    await scope.Transaction.RollbackAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await DisposeRootScopeAsync(scope).ConfigureAwait(false);
                }
            }
            else
            {
                await scope.Connection.ExecuteAsync(
                    $"ROLLBACK TO SAVEPOINT {_savepoint}", transaction: scope.Transaction)
                    .ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed) await RollbackAsync().ConfigureAwait(false);
        }

        private static async ValueTask DisposeRootScopeAsync(TxScope scope)
        {
            await scope.Transaction.DisposeAsync().ConfigureAwait(false);
            await scope.Connection.DisposeAsync().ConfigureAwait(false);
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
