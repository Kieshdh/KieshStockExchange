using Npgsql;

namespace KieshStockExchange.Server.Data;

/// <summary>
/// 7c — hands out pooled Npgsql connections. DBService takes this instead of
/// holding a single SQLiteAsyncConnection; each query opens, uses, and disposes
/// a connection (the NpgsqlDataSource underneath pools them). Transactions hold
/// one connection open for their lifetime via the AsyncLocal stack in DBService.
/// </summary>
public interface IDbConnectionFactory
{
    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default);
}
