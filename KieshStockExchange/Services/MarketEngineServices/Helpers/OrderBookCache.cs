using System.Collections.Concurrent;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface IOrderBookCache
{
    /// <summary> Get the order book for a specific stock and currency, ensuring it is loaded. </summary>
    Task<OrderBook> GetAsync(int stockId, CurrencyType currency, CancellationToken ct);

    /// <summary> Execute a function with exclusive access to the order book for a specific stock and currency. </summary>
    Task WithBookLockAsync(int stockId, CurrencyType currency, CancellationToken ct, Func<OrderBook, Task> body);

    /// <summary> Validate the index of the order book for a specific stock and currency. </summary>
    Task<(bool ok, string reason)> ValidateAsync(int stockId, CurrencyType currency, CancellationToken ct);

    /// <summary> Rebuild the index of the order book for a specific stock and currency. </summary>
    Task RebuildIndexAsync(int stockId, CurrencyType currency, CancellationToken ct);
}

public sealed class OrderBookCache : IOrderBookCache
{
    #region Dictionaries
    // Order book for each stock (keyed by stock ID and CurrencyType)
    // In-memory order books: price‐time priority
    // Buy: highest price first; Sell: lowest price first
    private readonly ConcurrentDictionary<(int, CurrencyType), OrderBook> _books = new();

    // Locks for loading order books from the database
    // The lock is per-stock, so different stocks can be processed in parallel.
    // The lock also protects loading the book from the database if needed.
    private readonly ConcurrentDictionary<(int, CurrencyType), SemaphoreSlim> _locks = new();

    // Show if the book has been loaded at least once
    private readonly ConcurrentDictionary<(int, CurrencyType), bool> _loaded = new();
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;

    public OrderBookCache(IDataBaseService db) => _db = db ?? throw new ArgumentNullException(nameof(db));
    #endregion

    #region Private Helpers
    // Get or create the lock for a specific stock and currency
    private SemaphoreSlim GetGate(int stockId, CurrencyType c) =>
        _locks.GetOrAdd((stockId, c), _ => new SemaphoreSlim(1, 1));

    // Get or create the order book for a specific stock and currency
    private OrderBook GetOrCreate(int stockId, CurrencyType c) =>
        _books.GetOrAdd((stockId, c), _ => new OrderBook(stockId, c));

    // Ensure the order book for a specific stock and currency is loaded with open limit orders
    private async Task EnsureLoadedAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        var key = (stockId, currency);
        // Check if already loaded
        if (_loaded.TryGetValue(key, out var ready) && ready) return;

        // Wait for the lock to load
        var gate = GetGate(stockId, currency);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double check if already loaded
            if (_loaded.TryGetValue(key, out ready) && ready) return;

            // Load all open limit orders from DB and populate the book
            var book = GetOrCreate(stockId, currency);
            var openLimits = await _db.GetOpenLimitOrders(stockId, currency, ct).ConfigureAwait(false);
            foreach (var o in openLimits) 
                book.UpsertOrder(o);

            _loaded[key] = true; // Mark as loaded
        }
        finally { gate.Release(); }
    }
    #endregion

    #region Public Methods
    /// <summary> Get the order book for a specific stock and currency, ensuring it is loaded </summary>
    public async Task<OrderBook> GetAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        await EnsureLoadedAsync(stockId, currency, ct).ConfigureAwait(false);
        return GetOrCreate(stockId, currency);
    }

    /// <summary> Execute a function with exclusive access to the order book for a specific stock and currency </summary>
    public async Task WithBookLockAsync(int stockId, CurrencyType currency, CancellationToken ct, Func<OrderBook, Task> body)
    {
        await EnsureLoadedAsync(stockId, currency, ct).ConfigureAwait(false);
        var gate = GetGate(stockId, currency);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var book = GetOrCreate(stockId, currency);
            await body(book).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    #endregion
}
