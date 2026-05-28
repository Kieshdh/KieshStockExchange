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

    public PgDBService(IDbConnectionFactory factory, ILogger<PgDBService> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Single point where region methods acquire a connection. 7c-5 will rebind
    // this to consult the AsyncLocal transaction stack so ambient-tx callers
    // share the same NpgsqlConnection + NpgsqlTransaction.
    private ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
        => _factory.OpenConnectionAsync(ct);

    #region Generic operations
    public Task ResetTableAsync<T>(CancellationToken ct = default) where T : new()
        => throw new NotImplementedException();

    public Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task DropAndRecreateAsync(bool keepBackup = false, CancellationToken ct = default)
        => throw new NotImplementedException();
    #endregion
}
