using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class ApiDataBaseService
{
    public async Task<List<Order>> GetOrdersAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Order>>("api/orders", ApiJsonOptions.Default, ct) ?? new();

    public async Task<(List<Order> Items, int Total)> GetOrdersPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, string? statusFilter, int? userIdFilter = null, int? stockIdFilter = null, string? sideFilter = null, string? typeFilter = null, IList<int>? excludeUserIds = null, CancellationToken ct = default)
    {
        var q = new Q().Add("skip", skip).Add("take", take).Add("sortKey", sortKey).Add("desc", desc)
            .Add("fromUtc", fromUtc).Add("toUtc", toUtc)
            .Add("statusFilter", statusFilter).Add("userIdFilter", userIdFilter).Add("stockIdFilter", stockIdFilter)
            .Add("sideFilter", sideFilter).Add("typeFilter", typeFilter)
            .AddEach("excludeUserIds", excludeUserIds);
        var page = await _http.GetFromJsonAsync<PageResponse<Order>>($"api/orders/page{q}", ApiJsonOptions.Default, ct);
        return (page?.Items.ToList() ?? new(), page?.Total ?? 0);
    }

    public Task<Order?> GetOrderById(int orderId, CancellationToken ct = default)
        => GetNullableAsync<Order>($"api/orders/{orderId}", ct);

    public async Task<List<Order>> GetOrdersByIds(List<int> orderIds, CancellationToken ct = default)
        => await PostListAsync<List<int>, Order>("api/orders/by-ids", orderIds, ct);

    public async Task<List<Order>> GetOrdersByUserId(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Order>>($"api/orders/by-user/{userId}", ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<Order>> GetOrdersByStockId(int stockId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Order>>($"api/orders/by-stock/{stockId}", ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<Order>> GetOpenLimitOrders(int stockId, CurrencyType currency, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Order>>($"api/orders/open-limit/{stockId}/{currency}", ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<Order>> GetOpenOrdersForUsersAsync(List<int> userIds, CancellationToken ct = default)
        => await PostListAsync<List<int>, Order>("api/orders/open-for-users", userIds, ct);

    // Server-only: the stop trigger watcher lives on the server; the client never enumerates armed stops.
    public Task<List<Order>> GetAllArmedStopsAsync(CancellationToken ct = default)
        => throw new NotSupportedException("GetAllArmedStopsAsync is server-only (stop watcher runs server-side).");

    // Server-only: the trailing-stop watermark flusher runs server-side.
    public Task UpdateTrailStateAsync(IReadOnlyList<(int OrderId, decimal Watermark, decimal StopPrice)> updates, CancellationToken ct = default)
        => throw new NotSupportedException("UpdateTrailStateAsync is server-only (stop watcher runs server-side).");

    public Task<List<Order>> GetBracketChildrenAsync(int parentOrderId, CancellationToken ct = default)
        => throw new NotSupportedException("GetBracketChildrenAsync is server-only (bracket coordinator runs server-side).");

    public Task<List<Order>> GetActiveBracketChildrenAsync(CancellationToken ct = default)
        => throw new NotSupportedException("GetActiveBracketChildrenAsync is server-only (bracket coordinator runs server-side).");

    public Task CreateOrder(Order order, CancellationToken ct = default)
        => PostWriteBackAsync("api/orders", order, (d, r) => { if (d.OrderId == 0) d.OrderId = r.OrderId; }, ct);

    public Task UpdateOrder(Order order, CancellationToken ct = default)
        => PutJsonAsync("api/orders", order, ct);

    public Task DeleteOrder(Order order, CancellationToken ct = default)
        => DeleteUrlAsync($"api/orders/{order.OrderId}", ct);

    public async Task<List<Transaction>> GetTransactionsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Transaction>>("api/transactions", ApiJsonOptions.Default, ct) ?? new();

    public async Task<(List<Transaction> Items, int Total)> GetTransactionsPageAsync(int skip, int take, string sortKey, bool desc, DateTime fromUtc, DateTime toUtc, int? userIdFilter = null, int? stockIdFilter = null, string? currencyFilter = null, IList<int>? excludeBuyerOrSellerIds = null, CancellationToken ct = default)
    {
        var q = new Q().Add("skip", skip).Add("take", take).Add("sortKey", sortKey).Add("desc", desc)
            .Add("fromUtc", fromUtc).Add("toUtc", toUtc)
            .Add("userIdFilter", userIdFilter).Add("stockIdFilter", stockIdFilter)
            .Add("currencyFilter", currencyFilter)
            .AddEach("excludeBuyerOrSellerIds", excludeBuyerOrSellerIds);
        var page = await _http.GetFromJsonAsync<PageResponse<Transaction>>($"api/transactions/page{q}", ApiJsonOptions.Default, ct);
        return (page?.Items.ToList() ?? new(), page?.Total ?? 0);
    }

    public Task<Transaction?> GetTransactionById(int transactionId, CancellationToken ct = default)
        => GetNullableAsync<Transaction>($"api/transactions/{transactionId}", ct);

    public async Task<List<Transaction>> GetTransactionsByUserId(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Transaction>>($"api/transactions/by-user/{userId}", ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<Transaction>> GetTransactionsByOrderId(int orderId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Transaction>>($"api/transactions/by-order/{orderId}", ApiJsonOptions.Default, ct) ?? new();

    // maxRows is enforced server-side by the controller (a fixed cap, not client-tunable),
    // so it isn't sent on the wire; the parameter exists only to satisfy the interface.
    public async Task<List<Transaction>> GetTransactionsByStockIdAndTimeRange(int stockId, CurrencyType currency, DateTime from, DateTime to, int? maxRows = null, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Transaction>>(
            $"api/transactions/by-stock-range/{stockId}/{currency}{new Q().Add("from", from).Add("to", to)}",
            ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<Transaction>> GetTransactionsSinceTime(DateTime since, int? limit = null, CancellationToken ct = default)
    {
        // Default cap = 10K rows. Bounds the worst-case response time observed during
        // the Phase 2 spike (375 ms – 2.1 s on unbounded payloads). Caller can override
        // by passing an explicit limit; null is intentionally not forwarded — that would
        // re-enable the unbounded path which the SignalR push (Phase 4) replaces anyway.
        var effective = limit ?? 10_000;
        return await _http.GetFromJsonAsync<List<Transaction>>(
            $"api/transactions/since{new Q().Add("since", since).Add("limit", effective)}",
            ApiJsonOptions.Default, ct) ?? new();
    }

    public Task<Transaction?> GetLatestTransactionByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
        => GetNullableAsync<Transaction>($"api/transactions/latest/{stockId}/{currency}", ct);

    public Task<Transaction?> GetLatestTransactionBeforeTime(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
        => GetNullableAsync<Transaction>($"api/transactions/latest-before/{stockId}/{currency}{new Q().Add("time", time)}", ct);

    public Task CreateTransaction(Transaction transaction, CancellationToken ct = default)
        => PostWriteBackAsync("api/transactions", transaction, (d, r) => { if (d.TransactionId == 0) d.TransactionId = r.TransactionId; }, ct);

    public Task UpdateTransaction(Transaction transaction, CancellationToken ct = default)
        => PutJsonAsync("api/transactions", transaction, ct);

    public Task DeleteTransaction(Transaction transaction, CancellationToken ct = default)
        => DeleteUrlAsync($"api/transactions/{transaction.TransactionId}", ct);
}
