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

        // Apply a default pool ceiling only when the resolved string didn't set one,
        // so an explicit production value is always honored. Kept below Postgres
        // max_connections, with headroom above the engine's MaxConcurrentGroups cap.
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("Pool Size", StringComparison.OrdinalIgnoreCase))
            builder.MaxPoolSize = config.GetValue("Db:MaxPoolSize", 50);

        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        => await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
