using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// Server-backed drawings store with a local <c>Preferences</c> cache (UP-STORE, client side).
/// <list type="bullet">
/// <item>Local cache reuses the existing <c>chart_drawings_&lt;stockId&gt;_&lt;currency&gt;</c> key — the
/// instant/offline render layer and the migrate-up source.</item>
/// <item><see cref="Save"/> writes local immediately (the durability contract) and enqueues a
/// per-key, debounced, coalesced server push.</item>
/// <item><see cref="LoadAsync"/> is device-local-wins for v1: adopt the server copy only when local
/// is empty; when local exists but the server is empty, migrate-up seeds the account.</item>
/// </list>
/// Server calls never surface to the UI — a failed GET/POST/DELETE degrades to the local cache.
/// </summary>
public sealed class CachedDrawingStore : IDrawingStore
{
    private const string PrefKeyBase = "chart_drawings_";
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(3);

    private readonly ApiDrawingStore _api;
    private readonly ILogger<CachedDrawingStore> _logger;

    // Pending server writes: prefKey -> latest json (null => delete tombstone). Coalesced per key.
    private readonly ConcurrentDictionary<string, string?> _pending = new();
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);   // FlushAsync re-entrancy guard

    public CachedDrawingStore(IHttpClientFactory factory, ILogger<CachedDrawingStore> logger)
    {
        _api = new ApiDrawingStore(factory);
        _logger = logger;
    }

    private static string PrefKey(int stockId, string currency) => $"{PrefKeyBase}{stockId}_{currency}";
    private SemaphoreSlim KeyLock(string prefKey) => _keyLocks.GetOrAdd(prefKey, _ => new SemaphoreSlim(1, 1));

    private static string? ReadLocal(string prefKey)
    {
        var s = Preferences.Default.Get(prefKey, string.Empty);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public async Task<string?> LoadAsync(int stockId, string currency)
    {
        var prefKey = PrefKey(stockId, currency);
        var gate = KeyLock(prefKey);   // serialize local read/write against a concurrent Save
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var local = ReadLocal(prefKey);

            string? server;
            try { server = await _api.GetAsync(stockId, currency, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // Offline / transient: local cache is the render source.
                _logger.LogDebug(ex, "Drawings server load failed; using local cache.");
                return local;
            }

            if (local is null)
            {
                // Adopt the server copy (if any) and seed local for instant subsequent renders.
                if (server is not null) Preferences.Default.Set(prefKey, server);
                return server;
            }

            // Local present → device-local-wins. If the server has nothing, migrate-up seeds the account.
            if (server is null)
            {
                _pending[prefKey] = local;
                ScheduleFlush(stockId, currency, prefKey);
            }
            return local;
        }
        finally { gate.Release(); }
    }

    public void Save(int stockId, string currency, string json)
    {
        var prefKey = PrefKey(stockId, currency);
        // Synchronous local write = the durability guarantee (instant read-your-writes, offline-safe).
        try { Preferences.Default.Set(prefKey, json); }
        catch (Exception ex) { _logger.LogDebug(ex, "Local drawings write failed."); }

        _pending[prefKey] = json;
        ScheduleFlush(stockId, currency, prefKey);
    }

    public async Task DeleteAsync(int stockId, string currency)
    {
        var prefKey = PrefKey(stockId, currency);
        try { Preferences.Default.Remove(prefKey); } catch { /* best-effort */ }
        _pending[prefKey] = null;   // tombstone; PushKeyAsync issues the DELETE
        ScheduleFlush(stockId, currency, prefKey);
    }

    public async Task FlushAsync()
    {
        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var prefKey in _pending.Keys.ToList())
                if (TryParseKey(prefKey, out var stockId, out var currency))
                    await PushKeyAsync(stockId, currency, prefKey).ConfigureAwait(false);
        }
        finally { _flushGate.Release(); }
    }

    // Debounce: (re)arm a one-shot per-key timer; rapid edits coalesce into one push.
    private void ScheduleFlush(int stockId, string currency, string prefKey)
    {
        _timers.AddOrUpdate(prefKey,
            _ => new Timer(_ => _ = PushKeyAsync(stockId, currency, prefKey), null, DebounceDelay, Timeout.InfiniteTimeSpan),
            (_, existing) => { existing.Change(DebounceDelay, Timeout.InfiniteTimeSpan); return existing; });
    }

    private async Task PushKeyAsync(int stockId, string currency, string prefKey)
    {
        var gate = KeyLock(prefKey);
        await gate.WaitAsync().ConfigureAwait(false);   // at most one in-flight push per key (avoids reordering)
        try
        {
            if (!_pending.TryGetValue(prefKey, out var json)) return;
            try
            {
                if (json is null) await _api.DeleteAsync(stockId, currency, CancellationToken.None).ConfigureAwait(false);
                else await _api.PostAsync(stockId, currency, json, CancellationToken.None).ConfigureAwait(false);
                // Remove only if unchanged since we read it — a newer Save stays pending for the next push.
                _pending.TryRemove(new KeyValuePair<string, string?>(prefKey, json));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Drawings server push failed; retried on next save/flush.");
            }
        }
        finally
        {
            gate.Release();
            if (_timers.TryRemove(prefKey, out var t)) t.Dispose();   // singleton store — don't leak timers
        }
    }

    private static bool TryParseKey(string prefKey, out int stockId, out string currency)
    {
        stockId = 0; currency = "";
        if (!prefKey.StartsWith(PrefKeyBase, StringComparison.Ordinal)) return false;
        var rest = prefKey.Substring(PrefKeyBase.Length);   // "{stockId}_{currency}"
        var us = rest.IndexOf('_');
        if (us <= 0) return false;
        if (!int.TryParse(rest.Substring(0, us), out stockId)) return false;
        currency = rest.Substring(us + 1);
        return currency.Length > 0;
    }
}
