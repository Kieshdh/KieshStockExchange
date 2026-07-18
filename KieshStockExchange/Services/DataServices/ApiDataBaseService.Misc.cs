using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

public sealed partial class ApiDataBaseService
{
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

    public Task<UserPreferences?> GetUserPreferencesByUserId(int userId, CancellationToken ct = default)
        => GetNullableAsync<UserPreferences>($"api/user-preferences/by-user/{userId}", ct);

    public Task UpsertUserPreferences(UserPreferences prefs, CancellationToken ct = default)
        => PutJsonAsync("api/user-preferences/upsert", prefs, ct);

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
}
