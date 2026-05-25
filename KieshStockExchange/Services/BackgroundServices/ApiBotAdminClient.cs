using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices;

namespace KieshStockExchange.Services.BackgroundServices;

// Phase 3 Step 7b.1 — thin HTTP wrapper over the server's /api/admin/bots/*
// surface. Drives the BotDashboard once Step 7b.2 swaps it in for the
// IAiTradeService + IUserSessionService.Start/StopBotsAsync pattern.
//
// No interface yet — the dashboard takes this class directly to avoid creating
// another contract we'd just delete in Wave 8.10's eventual ApiBotAdmin
// abstraction. Promote to an interface if/when a second consumer appears.
public sealed class ApiBotAdminClient
{
    private readonly HttpClient _http;

    public ApiBotAdminClient(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("KSE.Server");
    }

    public async Task<BotStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("api/admin/bots/status", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<BotStatusResponse>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync("api/admin/bots/start", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync("api/admin/bots/stop", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task UpdateScalerAsync(BotScalerSettings settings, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/admin/bots/scaler", settings, ApiJsonOptions.Default, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyCollection<int>> GetAiUserIdsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("api/admin/bots/ai-user-ids", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<int>>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
               ?? (IReadOnlyCollection<int>)Array.Empty<int>();
    }

    public async Task<IReadOnlyList<BotActivitySample>> GetActivitySamplesAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("api/admin/bots/activity-samples", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<BotActivitySample>>(ApiJsonOptions.Default, ct).ConfigureAwait(false)
               ?? (IReadOnlyList<BotActivitySample>)Array.Empty<BotActivitySample>();
    }
}
