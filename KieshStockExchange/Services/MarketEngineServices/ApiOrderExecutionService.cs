using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketEngineServices.CommandDtos;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

// Phase 3 Step 7: HTTP proxy for IOrderExecutionService. The admin VMs and a
// few engine call paths use this surface directly (vs the higher-level
// IOrderEntryService); they keep their interface, only the impl moves to HTTP.
// PlaceAndMatchAsync / PlaceAndMatchBatchAsync / ModifyOrderAsync proxy through
// to the same /api/orders/* endpoints ApiOrderEntryClient hits. Match results
// come back fully populated from the server's in-process engine — fills and
// settlement state aren't observable client-side any more.
public sealed class ApiOrderExecutionService : IOrderExecutionService
{
    private readonly HttpClient _http;

    public ApiOrderExecutionService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("KSE.Server");
    }

    public async Task<OrderResult> PlaceAndMatchAsync(Order order, CancellationToken ct = default)
    {
        var req = OrderToPlaceRequest(order);
        var resp = await _http.PostAsJsonAsync("api/orders/place", req, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<OrderResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("place-order returned no body.");
    }

    public Task<IReadOnlyList<OrderResult>> PlaceAndMatchBatchAsync(IReadOnlyList<Order> orders, CancellationToken ct = default)
    {
        // Batch place is no longer a first-class API after Phase 3 — the server's
        // in-process engine groups orders per book. Client callers (if any) walk
        // the list and submit one PlaceAndMatchAsync per order. Bot batches are
        // server-internal now, so this method is effectively dead on the client.
        return BatchAsync(orders, ct);
        async Task<IReadOnlyList<OrderResult>> BatchAsync(IReadOnlyList<Order> orders, CancellationToken ct)
        {
            var results = new OrderResult[orders.Count];
            for (int i = 0; i < orders.Count; i++)
                results[i] = await PlaceAndMatchAsync(orders[i], ct).ConfigureAwait(false);
            return results;
        }
    }

    // §3.6 P2: arm/promote are server-internal (the arm goes through /api/orders/place with a
    // Stop* type; promotion is driven by the server-side StopTriggerWatcher). The client never
    // calls these directly on the execution surface.
    public Task<OrderResult> ArmStopAsync(Order incoming, CancellationToken ct = default)
        => throw new NotSupportedException("ArmStopAsync is server-side; place a Stop* order via /api/orders/place.");

    public Task<OrderResult> PromoteStopAsync(int orderId, CancellationToken ct = default)
        => throw new NotSupportedException("PromoteStopAsync is server-side (driven by StopTriggerWatcher).");

    // §3.6 P4: bracket placement is server-internal (via IOrderEntryService.PlaceBracketAsync →
    // /api/orders/place-bracket); never called on the client execution surface.
    public Task<OrderResult> PlaceBracketAsync(Order parent, Order stopLoss,
        IReadOnlyList<Order> takeProfits, CancellationToken ct = default)
        => throw new NotSupportedException("PlaceBracketAsync is server-side; use IOrderEntryService.PlaceBracketAsync.");

    // §3.6 P3: a stop modify goes through the user-facing IOrderEntryService.ModifyStopAsync →
    // /api/orders/{id}/modify-stop, not this execution surface. Mirrors ArmStop/PromoteStop.
    public Task<OrderResult> ModifyStopAsync(int orderId, int? newQuantity = null,
        decimal? newStopPrice = null, decimal? newLimitPrice = null, CancellationToken ct = default)
        => throw new NotSupportedException("ModifyStopAsync is server-side; use IOrderEntryService.ModifyStopAsync.");

    public async Task<OrderResult> CancelOrderAsync(int orderId, CancellationToken ct = default)
    {
        // The HTTP endpoint takes userId; admin-path callers don't have it readily
        // available so we send userId=0 — the server-side OrderEntryService.CancelOrderAsync
        // will look up the owner by orderId.
        var resp = await _http.PostAsync($"api/orders/{orderId}/cancel?userId=0", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<OrderResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("cancel-order returned no body.");
    }

    public async Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/orders/{orderId}/modify",
            new ModifyOrderRequest(0, newQuantity, newPrice), ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<OrderResult>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("modify-order returned no body.");
    }

    public async Task<IReadOnlyList<OrderResult>> CancelOrdersBatchAsync(IReadOnlyList<int> orderIds, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/orders/cancel-batch",
            new CancelBatchRequest(orderIds), ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var list = await resp.Content.ReadFromJsonAsync<List<OrderResult>>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
        return list ?? (IReadOnlyList<OrderResult>)Array.Empty<OrderResult>();
    }

    private static PlaceOrderRequest OrderToPlaceRequest(Order o)
        => new(
            UserId: o.UserId,
            StockId: o.StockId,
            Quantity: o.Quantity,
            Side: o.Side,
            Entry: o.Entry,
            Stop: o.Stop,
            Currency: o.CurrencyType,
            // Limit (incl. stop-limit) and a capped market carry a Price/anchor; true market does not.
            Price: o.Entry == EntryType.Limit ? o.Price : (o.SlippagePercent.HasValue ? o.Price : (decimal?)null),
            SlippagePct: o.SlippagePercent,
            BuyBudget: o.BuyBudget,
            StopPrice: o.StopPrice);
}
