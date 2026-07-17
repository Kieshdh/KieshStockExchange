namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// Per-user chart-drawings store (UP-STORE, client side). Works at the raw <c>{ "v":1, ... }</c>
/// JSON-string level to match the opaque server contract — <c>ChartViewModel</c> keeps the
/// serialize/deserialize. The real impl (<see cref="CachedDrawingStore"/>) is server-backed with a
/// local <c>Preferences</c> cache: the synchronous local write inside <see cref="Save"/> is the
/// durability guarantee, the server copy is eventually-consistent and reconciled on next load.
/// </summary>
public interface IDrawingStore
{
    /// <summary>Local-first load (device-local-wins for v1); adopts + seeds from the server only when local is empty.</summary>
    Task<string?> LoadAsync(int stockId, string currency);

    /// <summary>Writes the local cache immediately, then debounces + coalesces a server push.</summary>
    void Save(int stockId, string currency, string json);

    /// <summary>Removes local + server copies.</summary>
    Task DeleteAsync(int stockId, string currency);

    /// <summary>Force-push all pending debounced writes now (stock-switch / logout).</summary>
    Task FlushAsync();
}
