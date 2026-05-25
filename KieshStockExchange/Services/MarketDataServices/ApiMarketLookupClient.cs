using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Phase 3 finish — HTTP proxy over the server's MarketLookupController.
/// Replaces the byte-identical in-process MarketLookupService duplicate that
/// pretended to read from the engine's live registry (which now lives on the
/// server). Stock catalogue reads stay on the client via <see cref="IStockService"/>
/// so the common case is one local hit, no round-trip.
/// </summary>
public sealed class ApiMarketLookupClient : IMarketLookupService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IStockService _stocks;

    public ApiMarketLookupClient(IHttpClientFactory httpFactory, IStockService stocks)
    {
        _httpFactory = httpFactory;
        _stocks = stocks;
    }

    private HttpClient Http() => _httpFactory.CreateClient("KSE.Server");

    public async Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default)
    {
        if (_stocks.TryGetById(stockId, out var s)) return s;
        await _stocks.EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _stocks.TryGetById(stockId, out var s2) ? s2 : null;
    }

    public async Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        await _stocks.EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _stocks.All;
    }

    public Task<decimal?> GetLatestPriceFromStoreAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
        => Http().GetFromJsonAsync<decimal?>($"api/market-lookup/latest-price/{stockId}/{currency}", ApiJsonOptions.Default, ct);

    public async Task<decimal> GetDateTimePriceAsync(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
    {
        var url = $"api/market-lookup/price-at/{stockId}/{currency}?time={Uri.EscapeDataString(time.ToString("o"))}";
        return await Http().GetFromJsonAsync<decimal>(url, ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }

    public async Task<List<Transaction>> LoadHistoricalTicksAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var list = await Http().GetFromJsonAsync<List<Transaction>>(
            $"api/market-lookup/historical-ticks/{stockId}/{currency}", ApiJsonOptions.Default, ct).ConfigureAwait(false);
        return list ?? new List<Transaction>();
    }

    public async Task<(decimal Price, DateTime TimeUtc)> GetFallbackPriceAndTimeAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var dto = await Http().GetFromJsonAsync<FallbackPriceDto>(
            $"api/market-lookup/fallback-price/{stockId}/{currency}", ApiJsonOptions.Default, ct).ConfigureAwait(false);
        return dto is null ? (100m, TimeHelper.NowUtc()) : (dto.Price, dto.TimeUtc);
    }

    // Mirror of the server-side response DTO; kept private to the client.
    private sealed record FallbackPriceDto(decimal Price, DateTime TimeUtc);
}
