using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class ApiDataBaseService
{
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
}
