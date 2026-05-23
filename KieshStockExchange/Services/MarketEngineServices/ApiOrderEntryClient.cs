using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

// Phase 3 Step 7: thin HTTP proxy over /api/orders/* that the server's
// OrderController exposes. Six Place*Async overloads serialize into the
// discriminated PlaceOrderRequest. ViewModels keep calling IOrderEntryService
// — only the impl behind the interface changes.
public sealed class ApiOrderEntryClient : IOrderEntryService
{
    private readonly HttpClient _http;

    public ApiOrderEntryClient(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("KSE.Server");
    }

    public Task<OrderResult> PlaceLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(new PlaceOrderRequest(userId, stockId, quantity, "LimitBuy", currency, limitPrice, null, null), ct);

    public Task<OrderResult> PlaceLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(new PlaceOrderRequest(userId, stockId, quantity, "LimitSell", currency, limitPrice, null, null), ct);

    public Task<OrderResult> PlaceSlippageMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal slippagePct, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(new PlaceOrderRequest(userId, stockId, quantity, "SlippageMarketBuy", currency, null, slippagePct, null), ct);

    public Task<OrderResult> PlaceSlippageMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal slippagePct, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(new PlaceOrderRequest(userId, stockId, quantity, "SlippageMarketSell", currency, null, slippagePct, null), ct);

    public Task<OrderResult> PlaceTrueMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(new PlaceOrderRequest(userId, stockId, quantity, "TrueMarketBuy", currency, null, null, buyBudget), ct);

    public Task<OrderResult> PlaceTrueMarketSellOrderAsync(int userId, int stockId, int quantity,
        CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(new PlaceOrderRequest(userId, stockId, quantity, "TrueMarketSell", currency, null, null, null), ct);

    public async Task<OrderResult> ModifyOrderAsync(int userId, int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/orders/{orderId}/modify",
            new ModifyOrderRequest(userId, newQuantity, newPrice), ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<OrderResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("modify-order returned no body.");
    }

    public async Task<OrderResult> CancelOrderAsync(int userId, int orderId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"api/orders/{orderId}/cancel?userId={userId}", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<OrderResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("cancel-order returned no body.");
    }

    private async Task<OrderResult> PlaceAsync(PlaceOrderRequest req, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/orders/place", req, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<OrderResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("place-order returned no body.");
    }
}
