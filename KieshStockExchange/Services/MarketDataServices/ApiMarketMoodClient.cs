using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// §market-mood: HTTP-backed mood read. Polls GET /api/market/mood/{stockId} on demand; the chart VM drives
/// the cadence and accumulates the samples into its live series. Returns null on any transport fault so the
/// pane just stalls rather than throwing.
/// </summary>
public sealed class ApiMarketMoodClient : IMarketMoodService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ApiMarketMoodClient> _logger;

    public ApiMarketMoodClient(IHttpClientFactory httpFactory, ILogger<ApiMarketMoodClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<double?> GetMoodAsync(int stockId, CancellationToken ct = default)
    {
        try
        {
            var http = _httpFactory.CreateClient("KSE.Server");
            var dto = await http.GetFromJsonAsync<StockMoodDto>(
                $"api/market/mood/{stockId}", ApiJsonOptions.Default, ct).ConfigureAwait(false);
            return dto?.Mood;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mood fetch failed for stock {StockId}.", stockId);
            return null;
        }
    }

    // Wire-shape mirror of the server's StockMoodResponse.
    private sealed record StockMoodDto(double Mood);
}
