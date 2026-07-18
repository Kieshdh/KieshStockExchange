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
public sealed partial class ApiDataBaseService : IDataBaseService
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

    // GET a JSON list, treating a null body as an empty list. Mirrors the hand-rolled
    // list-GET one-liner that used to be copy-pasted across the entity partials.
    private async Task<List<T>> GetListAsync<T>(string url, CancellationToken ct)
        => await _http.GetFromJsonAsync<List<T>>(url, ApiJsonOptions.Default, ct) ?? new();

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
}
