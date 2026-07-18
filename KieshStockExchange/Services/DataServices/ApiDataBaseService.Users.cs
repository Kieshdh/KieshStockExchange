using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class ApiDataBaseService
{
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
}
