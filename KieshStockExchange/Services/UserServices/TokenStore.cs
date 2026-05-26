using Microsoft.Maui.Storage;

namespace KieshStockExchange.Services.UserServices;

/// <summary>
/// Persistent token store backed by MAUI <see cref="SecureStorage"/>.
/// In-memory copy is kept for sync access on the hot path (HTTP handler,
/// SignalR AccessTokenProvider) — SecureStorage IO is only on
/// LoadAsync (boot) and SetAsync (login).
/// </summary>
public sealed class TokenStore
{
    private const string Key = "kse.jwt";
    private string? _current;
    private readonly object _lock = new();

    /// <summary>Synchronous accessor for the cached token. Null if not loaded or cleared.</summary>
    public string? Current { get { lock (_lock) return _current; } }

    /// <summary>Read SecureStorage on app startup so <see cref="Current"/> is populated before the first HTTP call.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var stored = await SecureStorage.GetAsync(Key).ConfigureAwait(false);
            lock (_lock) _current = stored;
        }
        catch { /* secure storage unavailable on this platform — fall back to in-memory only */ }
    }

    public async Task SetAsync(string token)
    {
        lock (_lock) _current = token;
        try { await SecureStorage.SetAsync(Key, token).ConfigureAwait(false); }
        catch { /* persistence best-effort */ }
    }

    public void Clear()
    {
        lock (_lock) _current = null;
        try { SecureStorage.Remove(Key); } catch { }
    }
}
