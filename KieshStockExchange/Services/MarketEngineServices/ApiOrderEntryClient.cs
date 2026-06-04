using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

// Phase 3 Step 7: thin HTTP proxy over /api/orders/*. §3.6 decomposition — each named
// Place*Async builds a PlaceOrderRequest carrying the (Side, Entry, Stop) dimensions + value
// fields; the server maps the combination back to the matching named method. ViewModels keep
// calling IOrderEntryService by name — only the request shape changed.
public sealed class ApiOrderEntryClient : IOrderEntryService
{
    private readonly HttpClient _http;

    public ApiOrderEntryClient(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("KSE.Server");
    }

    private PlaceOrderRequest Req(int userId, int stockId, int quantity, OrderSide side, EntryType entry,
        StopKind stop, CurrencyType currency, decimal? price = null, decimal? slippagePct = null,
        decimal? buyBudget = null, decimal? stopPrice = null)
        => new(userId, stockId, quantity, side, entry, stop, currency, price, slippagePct, buyBudget, stopPrice);

    public Task<OrderResult> PlaceLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Buy, EntryType.Limit, StopKind.None, currency, price: limitPrice), ct);

    public Task<OrderResult> PlaceLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Sell, EntryType.Limit, StopKind.None, currency, price: limitPrice), ct);

    public Task<OrderResult> PlaceSlippageMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal slippagePct, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Buy, EntryType.Market, StopKind.None, currency, slippagePct: slippagePct), ct);

    public Task<OrderResult> PlaceSlippageMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal slippagePct, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Sell, EntryType.Market, StopKind.None, currency, slippagePct: slippagePct), ct);

    public Task<OrderResult> PlaceTrueMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Buy, EntryType.Market, StopKind.None, currency, buyBudget: buyBudget), ct);

    public Task<OrderResult> PlaceTrueMarketSellOrderAsync(int userId, int stockId, int quantity,
        CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Sell, EntryType.Market, StopKind.None, currency), ct);

    public Task<OrderResult> PlaceStopMarketBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal buyBudget, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Buy, EntryType.Market, StopKind.Stop, currency, buyBudget: buyBudget, stopPrice: stopPrice), ct);

    public Task<OrderResult> PlaceStopMarketSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, CurrencyType currency, decimal? slippagePct = null, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Sell, EntryType.Market, StopKind.Stop, currency, slippagePct: slippagePct, stopPrice: stopPrice), ct);

    public Task<OrderResult> PlaceStopLimitBuyOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Buy, EntryType.Limit, StopKind.Stop, currency, price: limitPrice, stopPrice: stopPrice), ct);

    public Task<OrderResult> PlaceStopLimitSellOrderAsync(int userId, int stockId, int quantity,
        decimal stopPrice, decimal limitPrice, CurrencyType currency, CancellationToken ct = default)
        => PlaceAsync(Req(userId, stockId, quantity, OrderSide.Sell, EntryType.Limit, StopKind.Stop, currency, price: limitPrice, stopPrice: stopPrice), ct);

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
