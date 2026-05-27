using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KieshStockExchange.Server.HealthChecks;

/// <summary>
/// 7a-6 — readiness probe dependency. Confirms the database answers a trivial
/// read (the stock catalogue) so /healthz/ready only reports Healthy once the
/// DB is actually reachable.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDataBaseService _db;

    public DatabaseHealthCheck(IDataBaseService db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var stocks = await _db.GetStocksAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"database reachable ({stocks.Count} stocks)");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database query failed", ex);
        }
    }
}
