using Npgsql;

namespace KieshStockExchange.Server.Data;

/// <summary>
/// 7c — singleton wrapper over a pooled NpgsqlDataSource. Connection string
/// resolution order: ConnectionStrings:DefaultConnection, then the
/// KSE_DB_CONNECTION_STRING env var (set in production/containers), then a local
/// dev default. The data source owns the pool for the process lifetime.
/// </summary>
public sealed class PostgresConnectionFactory : IDbConnectionFactory, IAsyncDisposable
{
    private const string LocalDevDefault =
        "Host=localhost;Port=5432;Database=kse;Username=kse;Password=kse-dev";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresConnectionFactory(IConfiguration config)
    {
        var connectionString =
            config.GetConnectionString("DefaultConnection")
            ?? Environment.GetEnvironmentVariable("KSE_DB_CONNECTION_STRING")
            ?? LocalDevDefault;
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        => await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
