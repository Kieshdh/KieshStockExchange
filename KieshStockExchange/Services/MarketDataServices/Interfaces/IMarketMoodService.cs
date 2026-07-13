namespace KieshStockExchange.Services.MarketDataServices.Interfaces;

/// <summary>
/// §market-mood: client read of the server's ground-truth Fear/Greed field (0 = extreme fear, 50 = neutral,
/// 100 = extreme greed). On-demand fetch; the chart VM owns the poll cadence and accumulates the live series.
/// </summary>
public interface IMarketMoodService
{
    /// <summary>Current mood (0..100) for a stock, or null when the endpoint is unreachable.</summary>
    Task<double?> GetMoodAsync(int stockId, CancellationToken ct = default);
}
