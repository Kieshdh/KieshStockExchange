using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed class OrderBook
{
    #region Private Fields
    // Lock to prevent multiple users from unsynchronized decisions
    private readonly object _gate = new();

    // Buy side: highest price first (so we invert the default comparer)
    private readonly SortedDictionary<decimal, LinkedList<Order>> _buyBook
        = new (Comparer<decimal>.Create((a, b) => b.CompareTo(a)));

    // Sell side: lowest price first (default ascending order)
    private readonly SortedDictionary<decimal, LinkedList<Order>> _sellBook = new();

    // Fast index by OrderId so we can find & move/remove quickly
    private sealed class IndexEntry
    {
        public bool IsBuy;
        public decimal Price;
        public LinkedListNode<Order> Node = null!;
    }

    private readonly Dictionary<int, IndexEntry> _index = new();
    #endregion

    #region Public properties and Constructor
    public readonly int StockId;
    public readonly CurrencyType Currency;
    public event EventHandler<BookSnapshot>? Changed;

    public OrderBook(int stockId, CurrencyType currency)
    {
        StockId = stockId;
        Currency = currency;
    }
    #endregion

    #region Order management
    /// <summary>
    /// Insert if new and if it already exists, then it will update its price/side (and position).
    /// If the order is not an Open Limit order, it is removed from the book.
    /// </summary>
    public void UpsertOrder(Order incoming)
    {
        // Check order values
        CheckIncomingParameters(incoming);

        lock (_gate)
        {
            var book = incoming.IsBuyOrder ? _buyBook : _sellBook;

            // If this OrderId already lives in the book, we have an entry to update or remove.
            if (_index.TryGetValue(incoming.OrderId, out var idx))
            {
                // If it’s no longer an Open Limit order, drop it from the book.
                if (!incoming.IsOpen || !incoming.IsLimitOrder)
                {
                    RemoveIndexEntry(idx);
                    _index.Remove(incoming.OrderId);
                }
                else
                {
                    // Update the existing order in place, moving it if needed.
                    ReinsertOrder(idx, incoming);
                }
            }
            else
            {

                // If it gets here the order in not in the book.
                // But only add Open Limit orders to the book.
                if (!incoming.IsOpen || !incoming.IsLimitOrder)
                    return;

                // New order: add to the tail of its (side, price) level
                var newNode = new LinkedListNode<Order>(incoming);
                GetOrCreateLevel(book, incoming.Price).AddLast(newNode);

                _index[incoming.OrderId] = new IndexEntry
                {
                    IsBuy = incoming.IsBuyOrder,
                    Price = incoming.Price,
                    Node = newNode
                };
            }
        }
        NotifyChanged();
    }

    /// <summary> Removes an order by its ID. Returns true if removed, false if not found. </summary>
    public bool RemoveById(int orderId)
    {
        var removed = false;
        lock (_gate)
        {
            if (_index.TryGetValue(orderId, out var idx))
            {
                RemoveIndexEntry(idx);
                _index.Remove(orderId);
                removed = true;
            }
        }
        if (removed)
            NotifyChanged();
        return removed;
    }
    #endregion

    #region Other Methods
    /// <summary>
    /// Cheap, immutable snapshot of price levels for UI binding.
    /// Quantity is the sum of remaining quantities at each level.
    /// </summary>
    public BookSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new BookSnapshot
            {
                StockId = StockId,
                Buys = _buyBook.Select(
                    kv => new PriceLevel( kv.Key, kv.Value.Sum(o => o.RemainingQuantity) )
                ).ToList(),
                Sells = _sellBook.Select(
                    kv => new PriceLevel( kv.Key, kv.Value.Sum(o => o.RemainingQuantity) )
                ).ToList()
            };
        }
    }

    public Order? RemoveBestBuy(int? userId = null) => RemoveBest(_buyBook, userId);
    public Order? RemoveBestSell(int? userId = null) => RemoveBest(_sellBook, userId);

    public Order? PeekBestBuy(int? userId = null) => PeekBest(_buyBook, userId);
    public Order? PeekBestSell(int? userId = null) => PeekBest(_sellBook, userId);
    #endregion

    #region Admin Methods
    /// <summary>
    /// Fixes all detected inconsistencies in the order book and index. 
    /// Sends back a report of what was fixed/removed.
    /// </summary>
    public BookFixReport FixAll()
    {
        BookFixReport report;
        lock (_gate)
        {
            var buys = FixSide(_buyBook, isBuySide: true);
            var sells = FixSide(_sellBook, isBuySide: false);

            report = new BookFixReport(
                RemovedEmptyPriceLevelsBuys: buys.removedEmptyLevels,
                RemovedEmptyPriceLevelsSells: sells.removedEmptyLevels,
                RemovedOrphanedOrdersBuys: buys.removedOrphans,
                RemovedOrphanedOrdersSells: sells.removedOrphans,
                RemovedInvalidOrdersBuys: buys.removedInvalid,
                RemovedInvalidOrdersSells: sells.removedInvalid,
                RemovedNonOpenLimitBuys: buys.removedNonOpenLimit,
                RemovedNonOpenLimitSells: sells.removedNonOpenLimit,
                FixedIndexMismatchesBuys: buys.fixedIndexMismatches,
                FixedIndexMismatchesSells: sells.fixedIndexMismatches
            );
        }
        NotifyChanged();
        return report;
    }

    /// <summary> Lightweight consistency checker that does not modify anything. </summary>
    public bool ValidateIndex(out string reason)
    {
        lock (_gate)
        {
            // Every index entry must exist in its book at the price level with the same node
            foreach (var (orderId, idx) in _index)
            {
                var book = idx.IsBuy ? _buyBook : _sellBook;
                if (!book.TryGetValue(idx.Price, out var list))
                {
                    reason = $"Index points to missing level: order {orderId} @ {idx.Price}";
                    return false;
                }
                // Verify node membership
                bool found = false;
                for (var node = list.First; node != null; node = node.Next)
                {
                    if (node == idx.Node)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    reason = $"Index node not found in list for order {orderId} @ {idx.Price}";
                    return false;
                }
            }

            // Every order in books must be in the index with matching metadata
            foreach (var (sideName, book, isBuy) in EnumerateSides())
            {
                foreach (var kvp in book)
                {
                    foreach (var order in EnumerateList(kvp.Value))
                    {
                        if (!_index.TryGetValue(order.OrderId, out var idx))
                        {
                            reason = $"{sideName} orphan: order {order.OrderId} @ {kvp.Key}";
                            return false;
                        }
                        if (idx.IsBuy != isBuy || idx.Price != kvp.Key)
                        {
                            reason = $"{sideName} index mismatch: order {order.OrderId} @ {kvp.Key}";
                            return false;
                        }
                    }
                }
            }

            reason = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Nukes the _index and recreates it from the current books.
    /// Use if ValidateIndex fails and you want a best-effort repair.
    /// </summary>
    public void RebuildIndex()
    {
        lock (_gate)
        {
            _index.Clear();
            foreach (var (book, isBuy) in new[] { (_buyBook, true), (_sellBook, false) })
            {
                foreach (var kv in book)
                {
                    for (var node = kv.Value.First; node != null; node = node.Next)
                    {
                        var o = node.Value;
                        _index[o.OrderId] = new IndexEntry
                        {
                            IsBuy = isBuy,
                            Price = kv.Key,
                            Node = node
                        };
                    }
                }
            }
        }
        NotifyChanged();
    }
    #endregion

    #region Admin Helpers
    private (int removedEmptyLevels, int removedOrphans, int removedInvalid, int removedNonOpenLimit, int fixedIndexMismatches)
        FixSide(SortedDictionary<decimal, LinkedList<Order>> side, bool isBuySide)
    {
        int removedEmptyLevels = 0, removedOrphans = 0, removedInvalid = 0, removedNonOpenLimit = 0, fixedIndexMismatches = 0;

        // Work on a stable snapshot of price keys
        var priceKeys = side.Keys.ToList();

        foreach (var price in priceKeys)
        {
            if (!side.TryGetValue(price, out var list)) continue;
            if (list.Count == 0)
            {
                side.Remove(price);
                removedEmptyLevels++;
                continue;
            }

            // Collect what to remove + what to fix
            var nodesToRemove = new List<LinkedListNode<Order>>();

            for (var node = list.First; node != null; node = node.Next)
            {
                var order = node.Value;

                // Index must exist
                if (!_index.TryGetValue(order.OrderId, out var idx))
                {
                    Debug.WriteLine($"[OrderBook] Orphan on {(isBuySide ? "BUY" : "SELL")} price {price} -> order #{order.OrderId}, removing.");
                    nodesToRemove.Add(node);
                    removedOrphans++;
                    continue;
                }

                // Index must match side/price
                if (idx.IsBuy != isBuySide || idx.Price != price || idx.Node != node)
                {
                    // If the node is correct but metadata stale, just fix the metadata
                    if (idx.Node == node)
                    {
                        idx.IsBuy = isBuySide;
                        idx.Price = price;
                        fixedIndexMismatches++;
                    }
                    else
                    {
                        // Index points elsewhere; safest to remove and let Upsert re-add
                        Debug.WriteLine($"[OrderBook] Index mismatch for order #{order.OrderId} at {price}, removing.");
                        nodesToRemove.Add(node);
                        _index.Remove(order.OrderId);
                        removedOrphans++; // treat as orphan removal
                        continue;
                    }
                }

                // Order must be valid + be an OPEN LIMIT for book residency
                if (!order.IsValid())
                {
                    Debug.WriteLine($"[OrderBook] Invalid order #{order.OrderId} at {price}, removing.");
                    nodesToRemove.Add(node);
                    _index.Remove(order.OrderId);
                    removedInvalid++;
                    continue;
                }
                if (!order.IsOpen || !order.IsLimitOrder || order.IsBuyOrder != isBuySide)
                {
                    Debug.WriteLine($"[OrderBook] Non-open-limit or wrong-side order #{order.OrderId} at {price}, removing.");
                    nodesToRemove.Add(node);
                    _index.Remove(order.OrderId);
                    removedNonOpenLimit++;
                    continue;
                }
            }

            // Apply removals
            foreach (var node in nodesToRemove)
                list.Remove(node);

            if (list.Count == 0)
            {
                side.Remove(price);
                removedEmptyLevels++;
            }
        }

        return (removedEmptyLevels, removedOrphans, removedInvalid, removedNonOpenLimit, fixedIndexMismatches);
    }

    // Safer LinkedList enumerator that tolerates removals after enumeration
    private static IEnumerable<Order> EnumerateList(LinkedList<Order> list)
    {
        for (var node = list.First; node != null; node = node.Next)
            yield return node.Value;
    }

    // Yield sides for DRY loops
    private IEnumerable<(string sideName, SortedDictionary<decimal, LinkedList<Order>> book, bool isBuy)> EnumerateSides()
    {
        yield return ("BUY", _buyBook, true);
        yield return ("SELL", _sellBook, false);
    }
    #endregion

    #region Best Order Helpers
    private Order? RemoveBest(SortedDictionary<decimal, LinkedList<Order>> side, int? excludeUserId)
    {
        Order? removed;
        lock (_gate)
        {
            if (excludeUserId.HasValue)
                removed = RemoveBestInclude(side, excludeUserId.Value);
            else
                removed = RemoveBestExclude(side);
        }

        if (removed != null)
            NotifyChanged();

        return removed;
    }

    private Order? RemoveBestExclude(SortedDictionary<decimal, LinkedList<Order>> side)
    {
        // If empty
        if (side.Count == 0) return null;

        // Get best price level
        var kv = side.First();
        var node = kv.Value.First;
        if (node is null) // Empty price level
        {
            side.Remove(kv.Key);
            return null;
        }

        // Remove the first order at this level
        var order = node.Value;

        kv.Value.RemoveFirst();
        if (kv.Value.Count == 0)
            side.Remove(kv.Key);

        _index.Remove(order.OrderId);
        return order;
    }

    private Order? RemoveBestInclude(SortedDictionary<decimal, LinkedList<Order>> side, int userId)
    {
        if (side.Count == 0) return null;

        // Skip self-orders (same userId) while still respecting best-price then FIFO.
        var priceKeys = side.Keys.ToList(); // stable snapshot since we may remove price levels
        foreach (var price in priceKeys)
        {
            // Try get the level list
            if (!side.TryGetValue(price, out var list) || list.Count == 0)
            {
                side.Remove(price);
                continue;
            }

            // Iterate orders at this price level
            for (var node = list.First; node != null; node = node.Next)
            {
                if (node.Value.UserId == userId)
                    continue; // skip own orders

                var order = node.Value;

                list.Remove(node);
                if (list.Count == 0)
                    side.Remove(price);

                _index.Remove(order.OrderId);
                return order;
            }
        }

        // No non-self orders exist on this side
        return null;
    }

    private Order? PeekBest(SortedDictionary<decimal, LinkedList<Order>> side, int? excludeUserId)
    {
        lock (_gate)
        {
            if (side.Count == 0) return null;

            // No exclusion; just return the best
            if (!excludeUserId.HasValue)
            {
                var kv = side.First();
                return kv.Value.First?.Value;
            }

            // Skip self-orders (same userId) while still respecting best-price then FIFO.
            foreach (var kv in side) // already best→worst due to SortedDictionary comparer
            {
                for (var node = kv.Value.First; node != null; node = node.Next)
                {
                    if (node.Value.UserId != excludeUserId.Value)
                        return node.Value;
                }
            }

            return null;
        }
    }
    #endregion

    #region Other Helpers
    private static LinkedList<Order> GetOrCreateLevel(SortedDictionary<decimal, LinkedList<Order>> book, decimal price)
    {
        if (!book.TryGetValue(price, out var list))
        {
            list = new LinkedList<Order>();
            book[price] = list;
        }
        return list;
    }

    private void RemoveIndexEntry(IndexEntry idx)
    {
        var book = idx.IsBuy ? _buyBook : _sellBook;
        if (book.TryGetValue(idx.Price, out var list))
        {
            list.Remove(idx.Node);
            if (list.Count == 0) book.Remove(idx.Price);
        }
    }

    private void CheckIncomingParameters(Order incoming)
    {
        if (incoming == null)
            throw new ArgumentNullException(nameof(incoming), "Order cannot be null.");
        if (!incoming.IsValid())
            throw new ArgumentException($"Cannot upsert an invalid Order #{incoming.OrderId}", nameof(incoming));
        if (incoming.StockId != StockId)
            throw new ArgumentException($"Order must match the book's stock ID {StockId}.", nameof(incoming));
        if (incoming.CurrencyType != Currency)
            throw new ArgumentException($"Order must match the book's CurrencyType {Currency.ToString()}", nameof(incoming));
    }

    private void ReinsertOrder(IndexEntry idx, Order order)
    {
        // Before touching idx.Node.Value, snapshot old state
        var oldIsBuy = idx.IsBuy;
        var oldPrice = idx.Price;
        var oldRemaining = idx.Node.Value.RemainingQuantity;

        // Set the object reference
        idx.Node.Value = order;

        bool sideChanged = oldIsBuy != order.IsBuyOrder;
        bool priceChanged = oldPrice != order.Price;
        bool increasedSize = !sideChanged && !priceChanged &&
                             order.RemainingQuantity > oldRemaining;

        // Check if the side or the price has changed
        if (sideChanged || priceChanged || increasedSize)
        {
            // Remove from the old index entry
            RemoveIndexEntry(new IndexEntry { IsBuy = oldIsBuy, Price = oldPrice, Node = idx.Node });

            // Reinsert at the tail of the new (side, price) level
            var newBook = order.IsBuyOrder ? _buyBook : _sellBook;
            GetOrCreateLevel(newBook, order.Price).AddLast(idx.Node);

            // Update index metadata
            idx.IsBuy = order.IsBuyOrder;
            idx.Price = order.Price;
        }
    }

    private void NotifyChanged() => Changed?.Invoke(this, Snapshot());
    #endregion
}

public sealed record PriceLevel(decimal Price, int Quantity);
public sealed record BookSnapshot
{
    public int StockId { get; init; }
    public List<PriceLevel> Buys { get; init; } = new();
    public List<PriceLevel> Sells { get; init; } = new();
}
public sealed record BookFixReport(
    int RemovedEmptyPriceLevelsBuys, int RemovedEmptyPriceLevelsSells,
    int RemovedOrphanedOrdersBuys, int RemovedOrphanedOrdersSells,
    int RemovedInvalidOrdersBuys, int RemovedInvalidOrdersSells,
    int RemovedNonOpenLimitBuys, int RemovedNonOpenLimitSells,
    int FixedIndexMismatchesBuys, int FixedIndexMismatchesSells
);

