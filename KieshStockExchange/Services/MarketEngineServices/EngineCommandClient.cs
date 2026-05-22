using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

// Thin HTTP wrapper over the six engine bundle endpoints. One method per endpoint;
// no engine logic lives here. Pairs with EngineController on the server.
public sealed class EngineCommandClient : IEngineCommandClient
{
    private readonly HttpClient _http;

    public EngineCommandClient(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("KSE.Server");
    }

    public async Task<SettleSingleOrderResult> SettleSingleOrderAsync(SettleSingleOrderCommand cmd, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/engine/settle-single-order", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SettleSingleOrderResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("settle-single-order returned no body.");
    }

    public async Task<PlaceOrdersBatchResult> PlaceOrdersBatchAsync(PlaceOrdersBatchCommand cmd, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/engine/place-orders-batch", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PlaceOrdersBatchResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("place-orders-batch returned no body.");
    }

    public async Task<SettleTradeGroupResult> SettleTradeGroupAsync(SettleTradeGroupCommand cmd, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/engine/settle-trade-group", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SettleTradeGroupResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("settle-trade-group returned no body.");
    }

    public async Task ApplyOrderChangeAsync(ApplyOrderChangeCommand cmd, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/engine/apply-order-change", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<bool> DepositWithdrawAsync(DepositWithdrawCommand cmd, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/portfolio/deposit-withdraw", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<bool>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }

    public async Task<bool> ConvertInternalAsync(ConvertInternalCommand cmd, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/portfolio/convert-internal", cmd, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<bool>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }
}
