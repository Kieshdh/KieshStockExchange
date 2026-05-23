using System.Collections.ObjectModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

public sealed class StockService : IStockService
{
    #region Snapshot Fields
    // Snapshot fields are replaced atomically under lock; readers always see a consistent set.
    private IReadOnlyList<Stock> _all = Array.Empty<Stock>();
    public IReadOnlyList<Stock> All => _all;

    private IReadOnlyDictionary<int, Stock> _byId = new ReadOnlyDictionary<int, Stock>(new Dictionary<int, Stock>());
    public IReadOnlyDictionary<int, Stock> ById => _byId;

    private IReadOnlyDictionary<string, Stock> _bySymbol = new ReadOnlyDictionary<string, Stock>(new Dictionary<string, Stock>(StringComparer.OrdinalIgnoreCase));
    public IReadOnlyDictionary<string, Stock> BySymbol => _bySymbol;

    // Listings keyed by StockId. The "primary" currency lookup uses a separate
    // dict so TryGetCurrency stays O(1) without re-scanning the listings list.
    private IReadOnlyDictionary<int, IReadOnlyList<StockListing>> _listingsByStockId =
        new ReadOnlyDictionary<int, IReadOnlyList<StockListing>>(new Dictionary<int, IReadOnlyList<StockListing>>());

    private IReadOnlyDictionary<int, CurrencyType> _primaryCurrencyByStockId =
        new ReadOnlyDictionary<int, CurrencyType>(new Dictionary<int, CurrencyType>());

    public event EventHandler? CatalogChanged;

    private static readonly IEqualityComparer<string> SymbolComparer = StringComparer.OrdinalIgnoreCase;

    #endregion

    #region Fields and Constructor
    private readonly IDataBaseService _db;

    // Semaphores and locks for thread-safety.
    private readonly object _gate = new();
    private volatile bool _loaded;
    private readonly SemaphoreSlim _loadOnce = new(1, 1);

    public StockService(IDataBaseService db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }
    #endregion

    #region Loading and Refreshing
    public async Task<bool> EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return true;
        await _loadOnce.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return true;
            var loaded = await RefreshAsync(ct).ConfigureAwait(false);
            _loaded = loaded;
            return loaded;
        }
        finally { _loadOnce.Release(); }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            // Load all stocks + listings from the DB in parallel.
            var stocksTask = _db.GetStocksAsync(ct);
            var listingsTask = _db.GetStockListingsAsync(ct);
            await Task.WhenAll(stocksTask, listingsTask).ConfigureAwait(false);
            var stocks = stocksTask.Result;
            var listings = listingsTask.Result;

            // Build fresh maps.
            var byId = new Dictionary<int, Stock>(stocks.Count);
            var bySymbol = new Dictionary<string, Stock>(SymbolComparer);
            foreach (var stock in stocks)
            {
                // Skip invalid entries.
                if (stock.StockId <= 0 || stock.IsInvalid)
                    continue;
                // Insert into both maps.
                byId[stock.StockId] = stock;
                bySymbol[stock.Symbol] = stock;
            }

            // Group listings by StockId. Discard listings whose StockId isn't
            // in the byId map (cleaned-up rows from a previous run could
            // otherwise leak into the snapshot).
            var listingsByStockId = new Dictionary<int, IReadOnlyList<StockListing>>();
            var primaryByStockId = new Dictionary<int, CurrencyType>();
            foreach (var grp in listings.Where(l => l.IsValid() && byId.ContainsKey(l.StockId))
                                        .GroupBy(l => l.StockId))
            {
                var list = grp.ToList();
                listingsByStockId[grp.Key] = list;
                var primary = list.FirstOrDefault(l => l.IsPrimary) ?? list[0];
                primaryByStockId[grp.Key] = primary.CurrencyType;
            }

            // Sort the list purely for prettier UI binding.
            var all = byId.Values.OrderBy(s => s.Symbol).ToList();

            // Swap snapshots under a tiny lock so readers always see a consistent set.
            lock (_gate)
            {
                _all = all;
                _byId = new ReadOnlyDictionary<int, Stock>(byId);
                _bySymbol = new ReadOnlyDictionary<string, Stock>(bySymbol);
                _listingsByStockId = new ReadOnlyDictionary<int, IReadOnlyList<StockListing>>(listingsByStockId);
                _primaryCurrencyByStockId = new ReadOnlyDictionary<int, CurrencyType>(primaryByStockId);
            }

            // Notify listeners.
            CatalogChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch { return false; }
    }
    #endregion

    #region Snapshot Getters
    public bool TryGetById(int id, out Stock? stock) =>
        ById.TryGetValue(id, out stock);

    public bool TryGetBySymbol(string symbol, out Stock? stock)
    {
        // Check for null/empty input.
        stock = null;
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        // Trim and lookup in case-insensitive dictionary.
        return BySymbol.TryGetValue(symbol.Trim(), out stock);
    }

    public IReadOnlyList<Stock> Search(string? query, int take = 50)
    {
        var src = _all; // snapshot
        if (string.IsNullOrWhiteSpace(query))
            return src.Take(take).ToList();

        var q = query.Trim();
        return src
            .Where(s => s.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || s.CompanyName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Symbol)
            .Take(take)
            .ToList();
    }

    public bool TryGetSymbol(int id, out string symbol)
    {
        if (TryGetById(id, out var stock))
        {
            symbol = stock!.Symbol;
            return true;
        }
        symbol = string.Empty;
        return false;
    }

    public bool TryGetCurrency(int id, out CurrencyType currency)
    {
        if (_primaryCurrencyByStockId.TryGetValue(id, out var ccy))
        {
            currency = ccy;
            return true;
        }
        currency = CurrencyType.USD;
        return false;
    }

    public IReadOnlyList<StockListing> GetListings(int stockId) =>
        _listingsByStockId.TryGetValue(stockId, out var list)
            ? list
            : Array.Empty<StockListing>();

    public bool IsListedIn(int stockId, CurrencyType currency)
    {
        if (!_listingsByStockId.TryGetValue(stockId, out var list)) return false;
        for (int i = 0; i < list.Count; i++)
            if (list[i].CurrencyType == currency) return true;
        return false;
    }

    #endregion

    #region Upsert and Delete stocks
    public async Task<Stock> UpsertAsync(Stock stock, CancellationToken ct = default)
    {
        if (stock is null) throw new ArgumentNullException(nameof(stock));
        if (stock.IsInvalid) throw new ArgumentException("Invalid stock entity.", nameof(stock));

        // Persist to DB.
        await _db.UpsertStock(stock, ct).ConfigureAwait(false);

        // Refresh in-memory snapshot atomically.
        lock (_gate)
        {
            // Copy current maps so the swap stays atomic for readers.
            var byId = new Dictionary<int, Stock>(_byId);
            var bySymbol = _bySymbol.ToDictionary(kv => kv.Key, kv => kv.Value, SymbolComparer);

            // Remove old symbol mapping if we are replacing an existing row.
            if (byId.TryGetValue(stock.StockId, out var old) &&
                !string.Equals(old.Symbol, stock.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                bySymbol.Remove(old.Symbol);
            }
            // Insert/replace.
            byId[stock.StockId] = stock;
            bySymbol[stock.Symbol] = stock;

            _byId = new ReadOnlyDictionary<int, Stock>(byId);
            _bySymbol = new ReadOnlyDictionary<string, Stock>(bySymbol);
            _all = byId.Values.OrderBy(x => x.Symbol).ToList();
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return stock;
    }

    public async Task<bool> DeleteAsync(int stockId, CancellationToken ct = default)
    {
        if (stockId <= 0) return false;

        if (!_byId.TryGetValue(stockId, out var existing)) return false;

        await _db.DeleteStock(existing, ct).ConfigureAwait(false);

        lock (_gate)
        {
            var byId = new Dictionary<int, Stock>(_byId);
            var bySymbol = _bySymbol.ToDictionary(kv => kv.Key, kv => kv.Value, SymbolComparer);

            if (!byId.Remove(stockId)) return false;
            bySymbol.Remove(existing.Symbol);

            _byId = new ReadOnlyDictionary<int, Stock>(byId);
            _bySymbol = new ReadOnlyDictionary<string, Stock>(bySymbol);
            _all = byId.Values.OrderBy(x => x.Symbol).ToList();
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
    #endregion
}
