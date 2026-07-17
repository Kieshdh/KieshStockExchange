using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// HTTP transport for the opaque drawings blob. Serializes the <see cref="DrawingPayload"/> DTO
/// in BOTH directions (never a bare string — a raw <c>[FromBody] string</c> would require a
/// JSON-quoted body). Uses the <c>"KSE.Server"</c> named client, so <c>AuthHeaderHandler</c>
/// attaches the JWT automatically. Its helpers mirror <c>ApiDataBaseService</c>'s style +
/// <c>ApiJsonOptions.Default</c>, but are reimplemented here (those helpers are <c>private</c>).
/// </summary>
internal sealed class ApiDrawingStore
{
    private readonly HttpClient _http;

    public ApiDrawingStore(IHttpClientFactory factory) => _http = factory.CreateClient("KSE.Server");

    private static string Url(int stockId, string currency) => $"api/drawings/{stockId}/{currency}";

    /// <summary>GET → the raw envelope string, or null on 404 ("no drawing").</summary>
    public async Task<string?> GetAsync(int stockId, string currency, CancellationToken ct)
    {
        var resp = await _http.GetAsync(Url(stockId, currency), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<DrawingPayload>(ApiJsonOptions.Default, ct).ConfigureAwait(false);
        return dto?.Json;
    }

    /// <summary>POST the blob (server returns 202 Accepted; EnsureSuccessStatusCode accepts it).</summary>
    public async Task PostAsync(int stockId, string currency, string json, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync(Url(stockId, currency), new DrawingPayload(json), ApiJsonOptions.Default, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>DELETE (204 NoContent; a 404 is treated as already-gone).</summary>
    public async Task DeleteAsync(int stockId, string currency, CancellationToken ct)
    {
        var resp = await _http.DeleteAsync(Url(stockId, currency), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        resp.EnsureSuccessStatusCode();
    }
}
