using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class ApiDataBaseService
{
    public async Task<List<Stock>> GetStocksAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Stock>>("api/stocks", ApiJsonOptions.Default, ct) ?? new();

    public async Task<Stock?> GetStockById(int stockId, CancellationToken ct = default)
        => await GetNullableAsync<Stock>($"api/stocks/{stockId}", ct);

    public async Task<bool> StockExists(int stockId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<bool>($"api/stocks/{stockId}/exists", ApiJsonOptions.Default, ct);

    public Task CreateStock(Stock stock, CancellationToken ct = default)
        => PostWriteBackAsync("api/stocks", stock, (d, r) => { if (d.StockId == 0) d.StockId = r.StockId; }, ct);

    public Task UpdateStock(Stock stock, CancellationToken ct = default)
        => PutJsonAsync("api/stocks", stock, ct);

    public Task UpsertStock(Stock stock, CancellationToken ct = default)
        => PutWriteBackAsync("api/stocks/upsert", stock, (d, r) => { if (d.StockId == 0) d.StockId = r.StockId; }, ct);

    public Task DeleteStock(Stock stock, CancellationToken ct = default)
        => DeleteUrlAsync($"api/stocks/{stock.StockId}", ct);

    public async Task<List<StockListing>> GetStockListingsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<StockListing>>("api/stock-listings", ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<StockListing>> GetStockListingsByStockId(int stockId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<StockListing>>($"api/stock-listings/by-stock/{stockId}", ApiJsonOptions.Default, ct) ?? new();

    public Task CreateStockListing(StockListing listing, CancellationToken ct = default)
        => PostWriteBackAsync("api/stock-listings", listing, (d, r) => { if (d.ListingId == 0) d.ListingId = r.ListingId; }, ct);

    public async Task<List<StockPrice>> GetStockPricesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<StockPrice>>("api/stock-prices", ApiJsonOptions.Default, ct) ?? new();

    public Task<StockPrice?> GetStockPriceById(int stockPriceId, CancellationToken ct = default)
        => GetNullableAsync<StockPrice>($"api/stock-prices/{stockPriceId}", ct);

    public async Task<List<StockPrice>> GetStockPricesByStockId(int stockId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<StockPrice>>($"api/stock-prices/by-stock/{stockId}", ApiJsonOptions.Default, ct) ?? new();

    public Task<StockPrice?> GetLatestStockPriceByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
        => GetNullableAsync<StockPrice>($"api/stock-prices/latest/{stockId}/{currency}", ct);

    public Task<StockPrice?> GetLatestStockPriceBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
        => GetNullableAsync<StockPrice>($"api/stock-prices/latest-before/{stockId}/{currency}?time={Uri.EscapeDataString(time.ToString("O"))}", ct);

    public async Task<List<StockPrice>> GetStockPricesByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<StockPrice>>(
            $"api/stock-prices/by-stock-range/{stockId}/{currency}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}",
            ApiJsonOptions.Default, ct) ?? new();

    public Task CreateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
        => PostWriteBackAsync("api/stock-prices", stockPrice, (d, r) => { if (d.PriceId == 0) d.PriceId = r.PriceId; }, ct);

    public Task UpdateStockPrice(StockPrice stockPrice, CancellationToken ct = default)
        => PutJsonAsync("api/stock-prices", stockPrice, ct);

    public Task DeleteStockPrice(StockPrice stockPrice, CancellationToken ct = default)
        => DeleteUrlAsync($"api/stock-prices/{stockPrice.PriceId}", ct);
}
