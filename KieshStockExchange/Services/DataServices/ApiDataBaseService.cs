using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

// Phase 2 Step 3 stub. Every IDataBaseService method throws NotImplementedException
// until Step 4 wires the read endpoints. BeginTransactionAsync/RunInTransactionAsync
// stay throwing NotSupportedException permanently — multi-write transactions
// are routed through IEngineCommandClient bundle endpoints instead (Step 6).
//
// The HttpClient is held but unused at this step; it's plumbed early so DI
// validation surfaces config problems before the first real call.
public sealed class ApiDataBaseService : IDataBaseService
{
    private readonly HttpClient _http;

    public ApiDataBaseService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("KSE.Server");
    }

    // POST a body, expect a JSON list back. Used by "by ids" / "for users" methods where
    // the id list would be too long for a URL query string.
    private async Task<List<TItem>> PostListAsync<TBody, TItem>(string requestUri, TBody body, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync(requestUri, body, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<TItem>>(ApiJsonOptions.Default, ct).ConfigureAwait(false) ?? new();
    }

    private async Task<T?> GetNullableAsync<T>(string requestUri, CancellationToken ct) where T : class
    {
        var resp = await _http.GetAsync(requestUri, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }

    // POST entity body, deserialize server-returned entity, copy assigned PK back onto the
    // source instance — preserves the in-process LocalDBService.CreateX contract over HTTP.
    private async Task PostWriteBackAsync<T>(string url, T body, Action<T, T> writeback, CancellationToken ct) where T : class
    {
        var resp = await _http.PostAsJsonAsync(url, body, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var assigned = await resp.Content.ReadFromJsonAsync<T>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
        if (assigned != null) writeback(body, assigned);
    }

    // PUT entity body, expect 204.
    private async Task PutJsonAsync<T>(string url, T body, CancellationToken ct)
    {
        var resp = await _http.PutAsJsonAsync(url, body, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    // PUT entity body, server returns entity (Upsert path; may assign PK for "new" rows).
    private async Task PutWriteBackAsync<T>(string url, T body, Action<T, T> writeback, CancellationToken ct) where T : class
    {
        var resp = await _http.PutAsJsonAsync(url, body, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var assigned = await resp.Content.ReadFromJsonAsync<T>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
        if (assigned != null) writeback(body, assigned);
    }

    private async Task DeleteUrlAsync(string url, CancellationToken ct)
    {
        var resp = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    // Tiny URL-encoded query-string builder for the paged endpoints. Skips null/empty values
    // so callers can pass nullable filters straight in without ceremony. Returns "" when empty
    // so caller can concat unconditionally; first key gets "?", subsequent get "&".
    private sealed class Q
    {
        private readonly System.Text.StringBuilder _sb = new();
        public Q Add(string key, object? value)
        {
            if (value is null) return this;
            if (value is string s && string.IsNullOrEmpty(s)) return this;
            _sb.Append(_sb.Length == 0 ? '?' : '&').Append(key).Append('=')
                .Append(Uri.EscapeDataString(value is DateTime dt ? dt.ToString("O")
                    : value is bool b ? (b ? "true" : "false")
                    : value.ToString() ?? ""));
            return this;
        }
        public Q AddEach(string key, System.Collections.IEnumerable? values)
        {
            if (values is null) return this;
            foreach (var v in values) Add(key, v);
            return this;
        }
        public override string ToString() => _sb.ToString();
    }

    #region Generic operations
    // Bulk writes + resets are server-only; the client never bulk-writes, so these throw.
    public Task ResetTableAsync<T>(CancellationToken ct = default) where T : new()
        => throw new NotSupportedException("ResetTableAsync is server-only; the client does not bulk-reset tables.");

    public Task InsertAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
        => throw new NotSupportedException("InsertAllAsync is server-only; the client does not bulk-insert.");

    public Task UpdateAllAsync<T>(IEnumerable<T> items, CancellationToken ct = default)
        => throw new NotSupportedException("UpdateAllAsync is server-only; the client does not bulk-update.");

    public async Task DropAndRecreateAsync(bool keepBackup = false, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"api/admin/drop-recreate?keepBackup={(keepBackup ? "true" : "false")}", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    // Per Phase 2 plan: transactions don't survive the HTTP boundary. Engine multi-writes
    // go through IEngineCommandClient instead. These two stay throwing for the entire phase.
    public Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
        => Task.FromException<ITransaction>(new NotSupportedException(
            "Use IEngineCommandClient for multi-writes; HTTP transport doesn't carry SQLite transactions."));

    public Task RunInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException(
            "Use IEngineCommandClient for multi-writes; HTTP transport doesn't carry SQLite transactions."));
    #endregion

    #region User operations
    public async Task<List<User>> GetUsersAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<User>>("api/users", ApiJsonOptions.Default, ct) ?? new();

    public async Task<(List<User> Items, int Total)> GetUsersPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        var url = $"api/users/page?skip={skip}&take={take}&sortKey={Uri.EscapeDataString(sortKey ?? "")}&desc={desc}"
            + (string.IsNullOrEmpty(filter) ? "" : $"&filter={Uri.EscapeDataString(filter)}");
        var page = await _http.GetFromJsonAsync<PageResponse<User>>(url, ApiJsonOptions.Default, ct);
        return (page?.Items.ToList() ?? new(), page?.Total ?? 0);
    }

    public Task<User?> GetUserById(int userId, CancellationToken ct = default)
        => GetNullableAsync<User>($"api/users/{userId}", ct);

    public Task<User?> GetUserByUsername(string username, CancellationToken ct = default)
        => GetNullableAsync<User>($"api/users/by-username/{Uri.EscapeDataString(username)}", ct);

    public async Task<List<User>> GetUsersByIds(IReadOnlyList<int> userIds, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/users/by-ids", userIds, ApiJsonOptions.Default, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<User>>(ApiJsonOptions.Default, ct) ?? new();
    }

    public async Task<bool> UserExists(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<bool>($"api/users/{userId}/exists", ApiJsonOptions.Default, ct);

    public Task CreateUser(User user, CancellationToken ct = default)
        => PostWriteBackAsync("api/users", user, (d, r) => { if (d.UserId == 0) d.UserId = r.UserId; }, ct);

    public Task UpdateUser(User user, CancellationToken ct = default)
        => PutJsonAsync("api/users", user, ct);

    public Task UpsertUser(User user, CancellationToken ct = default)
        => PutWriteBackAsync("api/users/upsert", user, (d, r) => { if (d.UserId == 0) d.UserId = r.UserId; }, ct);

    public Task DeleteUser(User user, CancellationToken ct = default)
        => DeleteUrlAsync($"api/users/{user.UserId}", ct);

    public Task DeleteUserById(int userId, CancellationToken ct = default)
        => DeleteUrlAsync($"api/users/{userId}/by-id", ct);
    #endregion

    #region Stock operations
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
    #endregion

    #region StockListing operations
    public async Task<List<StockListing>> GetStockListingsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<StockListing>>("api/stock-listings", ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<StockListing>> GetStockListingsByStockId(int stockId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<StockListing>>($"api/stock-listings/by-stock/{stockId}", ApiJsonOptions.Default, ct) ?? new();

    public Task CreateStockListing(StockListing listing, CancellationToken ct = default)
        => PostWriteBackAsync("api/stock-listings", listing, (d, r) => { if (d.ListingId == 0) d.ListingId = r.ListingId; }, ct);
    #endregion

    #region StockPrice operations
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
    #endregion

    #region Order operations
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

    public Task CreateOrder(Order order, CancellationToken ct = default)
        => PostWriteBackAsync("api/orders", order, (d, r) => { if (d.OrderId == 0) d.OrderId = r.OrderId; }, ct);

    public Task UpdateOrder(Order order, CancellationToken ct = default)
        => PutJsonAsync("api/orders", order, ct);

    public Task DeleteOrder(Order order, CancellationToken ct = default)
        => DeleteUrlAsync($"api/orders/{order.OrderId}", ct);
    #endregion

    #region Transaction operations
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
    #endregion

    #region Position operations
    public async Task<List<Position>> GetPositionsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Position>>("api/positions", ApiJsonOptions.Default, ct) ?? new();

    public async Task<(List<Position> Items, int Total)> GetPositionsPageAsync(int stockId, int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        var q = new Q().Add("skip", skip).Add("take", take).Add("sortKey", sortKey).Add("desc", desc).Add("filter", filter);
        var page = await _http.GetFromJsonAsync<PageResponse<Position>>($"api/positions/page/{stockId}{q}", ApiJsonOptions.Default, ct);
        return (page?.Items.ToList() ?? new(), page?.Total ?? 0);
    }

    public Task<Position?> GetPositionById(int positionId, CancellationToken ct = default)
        => GetNullableAsync<Position>($"api/positions/{positionId}", ct);

    public async Task<List<Position>> GetPositionsByUserId(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Position>>($"api/positions/by-user/{userId}", ApiJsonOptions.Default, ct) ?? new();

    public Task<Position?> GetPositionByUserIdAndStockId(int userId, int stockId, CancellationToken ct = default)
        => GetNullableAsync<Position>($"api/positions/by-user-stock/{userId}/{stockId}", ct);

    public async Task<List<Position>> GetPositionsForUsersAsync(List<int> userIds, CancellationToken ct = default)
        => await PostListAsync<List<int>, Position>("api/positions/for-users", userIds, ct);

    public Task CreatePosition(Position position, CancellationToken ct = default)
        => PostWriteBackAsync("api/positions", position, (d, r) => { if (d.PositionId == 0) d.PositionId = r.PositionId; }, ct);

    public Task UpdatePosition(Position position, CancellationToken ct = default)
        => PutJsonAsync("api/positions", position, ct);

    public Task DeletePosition(Position position, CancellationToken ct = default)
        => DeleteUrlAsync($"api/positions/{position.PositionId}", ct);

    public Task UpsertPosition(Position position, CancellationToken ct = default)
        => PutWriteBackAsync("api/positions/upsert", position, (d, r) => { if (d.PositionId == 0) d.PositionId = r.PositionId; }, ct);
    #endregion

    #region Fund operations
    public async Task<List<Fund>> GetFundsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Fund>>("api/funds", ApiJsonOptions.Default, ct) ?? new();

    public async Task<(List<int> UserIds, int Total)> GetFundsUserIdsPageAsync(int skip, int take, string sortKey, bool desc, string? filter, CancellationToken ct = default)
    {
        var q = new Q().Add("skip", skip).Add("take", take).Add("sortKey", sortKey).Add("desc", desc).Add("filter", filter);
        var page = await _http.GetFromJsonAsync<PageResponse<int>>($"api/funds/user-ids-page{q}", ApiJsonOptions.Default, ct);
        return (page?.Items.ToList() ?? new(), page?.Total ?? 0);
    }

    public async Task<(List<Fund> Items, int Total)> GetFundsPageAsync(int skip, int take, string sortKey, bool desc, int? userIdFilter = null, bool hasNonZero = false, bool hasReserved = false, string? currencyFilter = null, CancellationToken ct = default)
    {
        var q = new Q().Add("skip", skip).Add("take", take).Add("sortKey", sortKey).Add("desc", desc)
            .Add("userIdFilter", userIdFilter).Add("hasNonZero", hasNonZero).Add("hasReserved", hasReserved)
            .Add("currencyFilter", currencyFilter);
        var page = await _http.GetFromJsonAsync<PageResponse<Fund>>($"api/funds/page{q}", ApiJsonOptions.Default, ct);
        return (page?.Items.ToList() ?? new(), page?.Total ?? 0);
    }

    public Task<Fund?> GetFundById(int fundId, CancellationToken ct = default)
        => GetNullableAsync<Fund>($"api/funds/{fundId}", ct);

    public async Task<List<Fund>> GetFundsByUserId(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Fund>>($"api/funds/by-user/{userId}", ApiJsonOptions.Default, ct) ?? new();

    public Task<Fund?> GetFundByUserIdAndCurrency(int userId, CurrencyType currency, CancellationToken ct = default)
        => GetNullableAsync<Fund>($"api/funds/by-user-currency/{userId}/{currency}", ct);

    public async Task<List<Fund>> GetFundsForUsersAsync(List<int> userIds, CancellationToken ct = default)
        => await PostListAsync<List<int>, Fund>("api/funds/for-users", userIds, ct);

    public Task CreateFund(Fund fund, CancellationToken ct = default)
        => PostWriteBackAsync("api/funds", fund, (d, r) => { if (d.FundId == 0) d.FundId = r.FundId; }, ct);

    public Task UpdateFund(Fund fund, CancellationToken ct = default)
        => PutJsonAsync("api/funds", fund, ct);

    public Task DeleteFund(Fund fund, CancellationToken ct = default)
        => DeleteUrlAsync($"api/funds/{fund.FundId}", ct);

    public Task UpsertFund(Fund fund, CancellationToken ct = default)
        => PutWriteBackAsync("api/funds/upsert", fund, (d, r) => { if (d.FundId == 0) d.FundId = r.FundId; }, ct);
    #endregion

    #region Candle operations
    public async Task<List<Candle>> GetCandlesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Candle>>("api/candles", ApiJsonOptions.Default, ct) ?? new();

    public Task<Candle?> GetCandleById(int candleId, CancellationToken ct = default)
        => GetNullableAsync<Candle>($"api/candles/{candleId}", ct);

    public async Task<List<Candle>> GetCandlesByStockId(int stockId, CurrencyType currency, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Candle>>($"api/candles/by-stock/{stockId}/{currency}", ApiJsonOptions.Default, ct) ?? new();

    public async Task<List<Candle>> GetCandlesByStockIdAndTimeRange(int stockId, CurrencyType currency, TimeSpan resolution, DateTime from, DateTime to, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Candle>>(
            $"api/candles/by-stock-range/{stockId}/{currency}?resolution={Uri.EscapeDataString(resolution.ToString())}&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}",
            ApiJsonOptions.Default, ct) ?? new();

    public Task CreateCandle(Candle candle, CancellationToken ct = default)
        => PostWriteBackAsync("api/candles", candle, (d, r) => { if (d.CandleId == 0) d.CandleId = r.CandleId; }, ct);

    public Task UpdateCandle(Candle candle, CancellationToken ct = default)
        => PutJsonAsync("api/candles", candle, ct);

    public Task DeleteCandle(Candle candle, CancellationToken ct = default)
        => DeleteUrlAsync($"api/candles/{candle.CandleId}", ct);

    public Task UpsertCandle(Candle candle, CancellationToken ct = default)
        => PutJsonAsync("api/candles/upsert", candle, ct);

    public async Task UpsertCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
    {
        if (candles.Count == 0) return;
        var resp = await _http.PostAsJsonAsync("api/candles/upsert-batch", candles, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
    #endregion

    #region Message operations
    public async Task<List<Message>> GetMessagesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Message>>("api/messages", ApiJsonOptions.Default, ct) ?? new();

    public Task<Message?> GetMessageById(int messageId, CancellationToken ct = default)
        => GetNullableAsync<Message>($"api/messages/{messageId}", ct);

    public async Task<List<Message>> GetMessagesByUserId(int userId, bool onlyUnread = false, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<Message>>($"api/messages/by-user/{userId}{new Q().Add("onlyUnread", onlyUnread)}", ApiJsonOptions.Default, ct) ?? new();

    public async Task<int> GetUnreadMessageCount(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<int>($"api/messages/unread-count/{userId}", ApiJsonOptions.Default, ct);

    public Task CreateMessage(Message message, CancellationToken ct = default)
        => PostWriteBackAsync("api/messages", message, (d, r) => { if (d.MessageId == 0) d.MessageId = r.MessageId; }, ct);

    public Task UpdateMessage(Message message, CancellationToken ct = default)
        => PutJsonAsync("api/messages", message, ct);

    public Task DeleteMessage(Message message, CancellationToken ct = default)
        => DeleteUrlAsync($"api/messages/{message.MessageId}", ct);

    public async Task<bool> MarkMessageRead(int messageId, DateTime? readAtUtc = null, CancellationToken ct = default)
    {
        var url = $"api/messages/{messageId}/mark-read{new Q().Add("readAtUtc", readAtUtc)}";
        var resp = await _http.PostAsync(url, content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<bool>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }

    public async Task<int> MarkAllMessagesRead(int userId, DateTime? readAtUtc = null, CancellationToken ct = default)
    {
        var url = $"api/messages/users/{userId}/mark-all-read{new Q().Add("readAtUtc", readAtUtc)}";
        var resp = await _http.PostAsync(url, content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<int>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }
    #endregion

    #region FundTransaction operations
    public async Task<List<FundTransaction>> GetFundTransactionsByUserId(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<FundTransaction>>($"api/fund-transactions/by-user/{userId}", ApiJsonOptions.Default, ct) ?? new();

    public async Task<(List<FundTransaction> Items, int Total)> GetFundTransactionsPageAsync(int skip, int take, string sortKey, bool desc, int? userIdFilter = null, CancellationToken ct = default)
    {
        var q = new Q().Add("skip", skip).Add("take", take).Add("sortKey", sortKey).Add("desc", desc)
            .Add("userIdFilter", userIdFilter);
        var page = await _http.GetFromJsonAsync<PageResponse<FundTransaction>>($"api/fund-transactions/page{q}", ApiJsonOptions.Default, ct);
        return (page?.Items.ToList() ?? new(), page?.Total ?? 0);
    }

    public Task CreateFundTransaction(FundTransaction tx, CancellationToken ct = default)
        => PostWriteBackAsync("api/fund-transactions", tx, (d, r) => { if (d.FundTransactionId == 0) d.FundTransactionId = r.FundTransactionId; }, ct);
    #endregion

    #region UserPreferences operations
    public Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default)
        => GetNullableAsync<UserPreferences>($"api/user-preferences/by-user/{userId}", ct);

    public Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default)
        => PutJsonAsync("api/user-preferences/upsert", prefs, ct);
    #endregion

    #region UserWatchlist operations
    public async Task<List<UserWatchlistEntry>> GetWatchlistByUserId(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<UserWatchlistEntry>>($"api/user-watchlist/by-user/{userId}", ApiJsonOptions.Default, ct) ?? new();

    public Task UpsertWatchlistEntry(UserWatchlistEntry entry, CancellationToken ct = default)
        => PutWriteBackAsync("api/user-watchlist/upsert", entry, (d, r) => { if (d.Id == 0) d.Id = r.Id; }, ct);

    public async Task<bool> DeleteWatchlistEntry(int userId, int stockId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"api/user-watchlist/{userId}/{stockId}", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<bool>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }

    public async Task ReplaceWatchlistAsync(int userId, IReadOnlyList<UserWatchlistEntry> entries, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/user-watchlist/users/{userId}/replace", entries, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
    #endregion

    #region AIUser operations
    public async Task<List<AIUser>> GetAIUsersAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<AIUser>>("api/ai-users", ApiJsonOptions.Default, ct) ?? new();

    public Task<AIUser?> GetAIUserById(int aiUserId, CancellationToken ct = default)
        => GetNullableAsync<AIUser>($"api/ai-users/{aiUserId}", ct);

    public async Task<List<AIUser>> GetAIUsersByUserId(int userId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<AIUser>>($"api/ai-users/by-user/{userId}", ApiJsonOptions.Default, ct) ?? new();

    public Task CreateAIUser(AIUser aiUser, CancellationToken ct = default)
        => PostWriteBackAsync("api/ai-users", aiUser, (d, r) => { if (d.AiUserId == 0) d.AiUserId = r.AiUserId; }, ct);

    public Task UpdateAIUser(AIUser aiUser, CancellationToken ct = default)
        => PutJsonAsync("api/ai-users", aiUser, ct);

    public Task UpsertAIUser(AIUser aiUser, CancellationToken ct = default)
        => PutWriteBackAsync("api/ai-users/upsert", aiUser, (d, r) => { if (d.AiUserId == 0) d.AiUserId = r.AiUserId; }, ct);

    public Task DeleteAIUser(AIUser aiUser, CancellationToken ct = default)
        => DeleteUrlAsync($"api/ai-users/{aiUser.AiUserId}", ct);
    #endregion
}
