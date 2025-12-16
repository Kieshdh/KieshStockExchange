using KieshStockExchange.Models;

namespace KieshStockExchange.Services.DataServices;

/// <summary>
/// In-memory catalog of Stocks.
/// Thread-safe snapshots, fast lookups by Id or Symbol,
/// and simple CRUD that keeps memory and DB in sync.
/// </summary>
public interface IStockService
{
    #region Properties and Events
    /// <summary>Raised whenever the catalog snapshot is replaced.</summary>
    event EventHandler? CatalogChanged;

    /// <summary>Snapshot of all stocks (immutable to consumers).</summary>
    IReadOnlyList<Stock> All { get; }

    /// <summary>Snapshot dictionaries for O(1) lookups.</summary>
    IReadOnlyDictionary<int, Stock> ById { get; }
    IReadOnlyDictionary<string, Stock> BySymbol { get; }
    #endregion

    #region Loading Operations
    /// <summary>Loads once (no-op if already loaded).</summary>
    Task<bool> EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>Rebuild snapshot from the DB.</summary>
    Task<bool> RefreshAsync(CancellationToken ct = default);
    #endregion

    #region Lookup Operations
    /// <summary>
    /// Try to get a stock by its unique identifier from in-memory snapshot.
    /// If not found, await EnsureLoadedAsync and try again once.
    /// </summary>
    bool TryGetById(int id, out Stock? stock);

    /// <summary>
    /// Try to get a stock by its unique case-insensitive symbol from in-memory snapshot.
    /// If not found, await EnsureLoadedAsync and try again once.
    /// </summary>
    bool TryGetBySymbol(string symbol, out Stock? stock);

    /// <summary>
    /// Try to get a stock symbol by its unique identifier from in-memory snapshot.
    /// </summary>
    bool TryGetSymbol(int id, out string symbol);

    /// <summary>Simple symbol/company search.</summary>
    IReadOnlyList<Stock> Search(string? query, int take = 50);
    #endregion

    #region Database Operations
    /// <summary>Create or update a stock in DB and refresh in-memory snapshot entry.</summary>
    Task<Stock> UpsertAsync(Stock stock, CancellationToken ct = default);

    /// <summary>Delete a stock (DB + in-memory).</summary>
    Task<bool> DeleteAsync(int stockId, CancellationToken ct = default);
    #endregion
}
