namespace KieshStockExchange.Services.PortfolioServices.Interfaces;

/// <summary>
/// Per-user list of favorited stocks. Single source of truth for "is this
/// stock watched?" and the persisted ordering. Cached in memory after the
/// first <see cref="RefreshAsync"/>; toggles persist immediately and raise
/// <see cref="Changed"/> so UI rows can re-evaluate.
/// </summary>
public interface IWatchlistService
{
    /// <summary>Raised after the in-memory cache mutates (refresh, toggle, reorder).</summary>
    event EventHandler? Changed;

    /// <summary>Stock ids currently on the watchlist, in user-defined sort order.</summary>
    IReadOnlyList<int> GetStockIds();

    bool IsWatched(int stockId);

    /// <summary>Reload from storage for the active user. Idempotent.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Add the stock if absent, remove it if present. Returns the new state
    /// (true = now watched, false = removed).
    /// </summary>
    Task<bool> ToggleAsync(int stockId, CancellationToken ct = default);

    /// <summary>
    /// Persist a new sort order. The list must contain exactly the currently
    /// watched stock ids (no adds, no removes) — call <see cref="ToggleAsync"/>
    /// for membership changes.
    /// </summary>
    Task ReorderAsync(IReadOnlyList<int> stockIdsInOrder, CancellationToken ct = default);

    /// <summary>Drops the in-memory cache. Used on logout.</summary>
    void Clear();
}
