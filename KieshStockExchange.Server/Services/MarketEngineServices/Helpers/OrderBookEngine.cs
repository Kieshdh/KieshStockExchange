using System.Collections.Concurrent;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Engine-internal live-book state. Matcher, settler, bot decisions take this
/// for mutable book access. Diagnostics go through <see cref="IOrderBookAdmin"/>;
/// UI consumes <see cref="GetSnapshotAsync"/> via the read-side controller.
/// </summary>
public interface IOrderBookEngine
{
    /// <summary>Returns the live mutable book for engine-internal callers. Loads on first access.</summary>
    Task<OrderBook> GetAsync(int stockId, CurrencyType currency, CancellationToken ct);

    Task WithBookLockAsync(int stockId, CurrencyType currency, CancellationToken ct, Func<OrderBook, Task> body);

    /// <summary>Read-only depth snapshot for HTTP/SignalR clients.</summary>
    Task<OrderBookSnapshot> GetSnapshotAsync(int stockId, CurrencyType currency, CancellationToken ct);

    /// <summary>
    /// Snapshot of currently-live books — used by OrderBookBroadcaster to
    /// attach Changed handlers on every existing key at boot and on newly-
    /// created keys as they appear. Stable enumeration order is not promised.
    /// </summary>
    IEnumerable<OrderBook> EnumerateBooks();
}

public sealed class OrderBookEngine : IOrderBookEngine
{
    #region Dictionaries
    // Order book for each stock (keyed by stock ID and CurrencyType)
    // In-memory order books: price–time priority
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
    private readonly IOrderRegistry _registry;

    public OrderBookEngine(IDataBaseService db, IOrderRegistry registry)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }
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

            // Load all open limit orders from DB and populate the book.
            // BulkLoad takes the gate once + skips per-order index lookups since we know
            // this is a fresh book.
            var book = GetOrCreate(stockId, currency);
            var openLimits = await _db.GetOpenLimitOrders(stockId, currency, ct).ConfigureAwait(false);

            // Route every loaded order through the registry so the matcher and the
            // reservation reconciler see the same canonical instances. If AccountsCache
            // got there first for this OrderId, GetOrAdd returns its (already-seeded) ref.
            for (int i = 0; i < openLimits.Count; i++)
                openLimits[i] = _registry.GetOrAdd(openLimits[i]);

            book.BulkLoad(openLimits);

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
        OrderBook book;
        try
        {
            book = GetOrCreate(stockId, currency);
            await body(book).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        // Fire one Changed notification per atomic body. Outside the lock so
        // slow subscribers can't back-pressure matching.
        book.FlushChanged();
    }

    public async Task<OrderBookSnapshot> GetSnapshotAsync(int stockId, CurrencyType currency, CancellationToken ct)
    {
        await EnsureLoadedAsync(stockId, currency, ct).ConfigureAwait(false);
        var book = GetOrCreate(stockId, currency);
        // ToDepthSnapshot is sync under _gate; no need to take the per-book
        // SemaphoreSlim — that's reserved for mutating bodies that need
        // exclusive access. Snapshot is a point-in-time read.
        return book.ToDepthSnapshot();
    }

    public IEnumerable<OrderBook> EnumerateBooks() => _books.Values;

    #endregion
}
