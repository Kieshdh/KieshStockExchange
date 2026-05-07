using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

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

    // Per-price-level running quantity totals. Kept in sync with _buyBook/_sellBook so
    // Snapshot() doesn't have to LINQ-Sum every level on every call. Levels with zero
    // total are removed.
    private readonly Dictionary<decimal, int> _buyQtyByPrice = new();
    private readonly Dictionary<decimal, int> _sellQtyByPrice = new();

    // Per-user count of orders currently resident in each side. Lets PeekBest /
    // RemoveBestInclude take an O(1) fast path when the user has no self-orders to skip.
    private readonly Dictionary<int, int> _buySelfCount = new();
    private readonly Dictionary<int, int> _sellSelfCount = new();

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

    /// <summary>
    /// Raised when the book's content has changed. Sender-only — subscribers that need a
    /// snapshot should call <see cref="Snapshot"/> themselves, optionally debounced. This
    /// keeps the hot path (match + upsert) from allocating a new snapshot per mutation.
    /// </summary>
    public event EventHandler? Changed;

    // Set by mutating operations; cleared by FlushChanged.
    private int _dirty;

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
                    var existing = idx.Node.Value;
                    DebitLevelQty(idx.IsBuy, idx.Price, existing.RemainingQuantity);
                    DecrementSelfCount(idx.IsBuy, existing.UserId);
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

                CreditLevelQty(incoming.IsBuyOrder, incoming.Price, incoming.RemainingQuantity);
                IncrementSelfCount(incoming.IsBuyOrder, incoming.UserId);
            }
        }
        MarkDirty();
    }

    /// <summary> Removes an order by its ID. Returns true if removed, false if not found. </summary>
    public bool RemoveById(int orderId)
    {
        var removed = false;
        lock (_gate)
        {
            if (_index.TryGetValue(orderId, out var idx))
            {
                var existing = idx.Node.Value;
                DebitLevelQty(idx.IsBuy, idx.Price, existing.RemainingQuantity);
                DecrementSelfCount(idx.IsBuy, existing.UserId);
                RemoveIndexEntry(idx);
                _index.Remove(orderId);
                removed = true;
            }
        }
        if (removed) MarkDirty();
        return removed;
    }

    /// <summary>
    /// Apply a fill to a maker order that is currently resting in the book. Updates the
    /// maker's <c>AmountFilled</c>, debits the per-level total by <paramref name="qty"/>,
    /// and removes the maker from the book + index if it became fully filled. Returns
    /// <c>true</c> when the maker was removed.
    /// </summary>
    /// <remarks>
    /// Caller must already hold the per-book SemaphoreSlim from <c>OrderBookCache.WithBookLockAsync</c>.
    /// This is the only path through which the matching loop should mutate maker fill state,
    /// so the level totals stay consistent.
    /// </remarks>
    public bool ApplyMakerFill(Order maker, int qty)
    {
        bool removed = false;
        lock (_gate)
        {
            // Debit the level total first (maker is still in the book at this point).
            DebitLevelQty(maker.IsBuyOrder, maker.Price, qty);

            // Now mutate the maker's fill state.
            maker.Fill(qty);

            // If fully filled, unlink + index-remove. No further qty debit here — the
            // remaining qty on the order is now zero by construction.
            if (maker.IsClosed && _index.TryGetValue(maker.OrderId, out var idx))
            {
                DecrementSelfCount(idx.IsBuy, maker.UserId);
                var book = idx.IsBuy ? _buyBook : _sellBook;
                if (book.TryGetValue(idx.Price, out var list))
                {
                    list.Remove(idx.Node);
                    if (list.Count == 0) book.Remove(idx.Price);
                }
                _index.Remove(maker.OrderId);
                removed = true;
            }
        }
        MarkDirty();
        return removed;
    }

    /// <summary>
    /// Undo a previously-applied fill on a maker. If the maker was removed by the fill,
    /// re-insert it; otherwise just credit the per-level total back. Caller is responsible
    /// for restoring <c>AmountFilled</c> on the order before calling.
    /// </summary>
    public void RollbackMakerFill(Order maker, int filledQty, bool wasRemoved)
    {
        lock (_gate)
        {
            if (wasRemoved)
            {
                // Re-insert as if a fresh upsert. Skip the in-place path because the
                // index entry is gone.
                if (!maker.IsOpen || !maker.IsLimitOrder) return;

                var book = maker.IsBuyOrder ? _buyBook : _sellBook;
                var newNode = new LinkedListNode<Order>(maker);
                GetOrCreateLevel(book, maker.Price).AddLast(newNode);
                _index[maker.OrderId] = new IndexEntry
                {
                    IsBuy = maker.IsBuyOrder,
                    Price = maker.Price,
                    Node = newNode
                };

                CreditLevelQty(maker.IsBuyOrder, maker.Price, maker.RemainingQuantity);
                IncrementSelfCount(maker.IsBuyOrder, maker.UserId);
            }
            else
            {
                // Maker stayed in the book; just credit the level back by the filled qty.
                CreditLevelQty(maker.IsBuyOrder, maker.Price, filledQty);
            }
        }
        MarkDirty();
    }

    /// <summary>
    /// Bulk-insert resting orders during initial cold-load from the database. Takes the
    /// gate once, skips per-order index lookups, and marks dirty once at the end.
    /// </summary>
    /// <remarks>
    /// Only safe to call while no other code can observe this book — typically from
    /// <c>OrderBookCache.EnsureLoadedAsync</c> while the load gate is held.
    /// </remarks>
    public void BulkLoad(IReadOnlyList<Order> openLimits)
    {
        if (openLimits is null || openLimits.Count == 0) return;

        lock (_gate)
        {
            for (int i = 0; i < openLimits.Count; i++)
            {
                var o = openLimits[i];

                // Defensive: ignore anything that doesn't belong in the book.
                if (o is null || !o.IsValid()) continue;
                if (o.StockId != StockId || o.CurrencyType != Currency) continue;
                if (!o.IsOpen || !o.IsLimitOrder) continue;
                if (_index.ContainsKey(o.OrderId)) continue; // skip dups defensively

                var book = o.IsBuyOrder ? _buyBook : _sellBook;
                var newNode = new LinkedListNode<Order>(o);
                GetOrCreateLevel(book, o.Price).AddLast(newNode);

                _index[o.OrderId] = new IndexEntry
                {
                    IsBuy = o.IsBuyOrder,
                    Price = o.Price,
                    Node = newNode
                };

                CreditLevelQty(o.IsBuyOrder, o.Price, o.RemainingQuantity);
                IncrementSelfCount(o.IsBuyOrder, o.UserId);
            }
        }
        MarkDirty();
    }
    #endregion

    #region Other Methods
    /// <summary>
    /// Cheap, immutable snapshot of price levels for UI binding. Reads pre-aggregated
    /// per-level totals — no LINQ Sum per call.
    /// </summary>
    public BookSnapshot Snapshot()
    {
        lock (_gate)
        {
            var buys = new List<PriceLevel>(_buyBook.Count);
            foreach (var kv in _buyBook)
            {
                _buyQtyByPrice.TryGetValue(kv.Key, out var qty);
                buys.Add(new PriceLevel(kv.Key, qty));
            }

            var sells = new List<PriceLevel>(_sellBook.Count);
            foreach (var kv in _sellBook)
            {
                _sellQtyByPrice.TryGetValue(kv.Key, out var qty);
                sells.Add(new PriceLevel(kv.Key, qty));
            }

            return new BookSnapshot
            {
                StockId = StockId,
                Buys = buys,
                Sells = sells,
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

            // FixSide may have ripped orders out without touching the level totals or
            // self-counts; recompute both from scratch to stay consistent.
            RecomputeAggregates();
        }
        MarkDirty();
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
            // Rebuilding the index from books necessarily means the qty/self aggregates
            // could be out of sync too — recompute them from the canonical list state.
            RecomputeAggregates();
        }
        MarkDirty();
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

    // Recompute level qty totals and per-user self-counts from the canonical list contents.
    // Cheap: O(orders). Used after admin operations that bulk-mutate without going through
    // the normal credit/debit helpers.
    private void RecomputeAggregates()
    {
        _buyQtyByPrice.Clear();
        _sellQtyByPrice.Clear();
        _buySelfCount.Clear();
        _sellSelfCount.Clear();

        foreach (var (_, book, isBuy) in EnumerateSides())
        {
            var qtyDict = isBuy ? _buyQtyByPrice : _sellQtyByPrice;
            var selfDict = isBuy ? _buySelfCount : _sellSelfCount;
            foreach (var kv in book)
            {
                int levelTotal = 0;
                for (var node = kv.Value.First; node != null; node = node.Next)
                {
                    var o = node.Value;
                    levelTotal += o.RemainingQuantity;
                    selfDict.TryGetValue(o.UserId, out var c);
                    selfDict[o.UserId] = c + 1;
                }
                if (levelTotal > 0) qtyDict[kv.Key] = levelTotal;
            }
        }
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

        if (removed != null) MarkDirty();
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
        bool isBuy = ReferenceEquals(side, _buyBook);

        DebitLevelQty(isBuy, kv.Key, order.RemainingQuantity);
        DecrementSelfCount(isBuy, order.UserId);

        kv.Value.RemoveFirst();
        if (kv.Value.Count == 0)
            side.Remove(kv.Key);

        _index.Remove(order.OrderId);
        return order;
    }

    private Order? RemoveBestInclude(SortedDictionary<decimal, LinkedList<Order>> side, int userId)
    {
        if (side.Count == 0) return null;

        bool isBuy = ReferenceEquals(side, _buyBook);

        // Fast path: the user has no resting orders on this side, so every best price
        // level's first order is a non-self order.
        var selfDict = isBuy ? _buySelfCount : _sellSelfCount;
        if (!selfDict.TryGetValue(userId, out var selfCount) || selfCount == 0)
            return RemoveBestExclude(side);

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

                DebitLevelQty(isBuy, price, order.RemainingQuantity);
                DecrementSelfCount(isBuy, order.UserId);

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

            // Common case: no exclusion or user has no self-orders on this side. O(1).
            if (!excludeUserId.HasValue)
            {
                var kv = side.First();
                return kv.Value.First?.Value;
            }

            bool isBuy = ReferenceEquals(side, _buyBook);
            var selfDict = isBuy ? _buySelfCount : _sellSelfCount;
            if (!selfDict.TryGetValue(excludeUserId.Value, out var selfCount) || selfCount == 0)
            {
                var kv = side.First();
                return kv.Value.First?.Value;
            }

            // Slow path: skip self-orders (same userId) while still respecting best-price
            // then FIFO. Worst-case O(N), but only reached when the user is on this side.
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
        var oldUserId = idx.Node.Value.UserId;
        var oldRemaining = idx.Node.Value.RemainingQuantity;

        // Set the object reference
        idx.Node.Value = order;

        bool sideChanged = oldIsBuy != order.IsBuyOrder;
        bool priceChanged = oldPrice != order.Price;
        bool increasedSize = !sideChanged && !priceChanged &&
                             order.RemainingQuantity > oldRemaining;

        if (sideChanged || priceChanged)
        {
            // Old level loses the entire old qty; new level gains the new qty.
            DebitLevelQty(oldIsBuy, oldPrice, oldRemaining);
            CreditLevelQty(order.IsBuyOrder, order.Price, order.RemainingQuantity);

            // Self-counts: if the user changed too, decrement old / increment new.
            if (oldUserId != order.UserId || sideChanged)
            {
                DecrementSelfCount(oldIsBuy, oldUserId);
                IncrementSelfCount(order.IsBuyOrder, order.UserId);
            }

            // Remove from the old (side, price) bucket
            RemoveIndexEntry(new IndexEntry { IsBuy = oldIsBuy, Price = oldPrice, Node = idx.Node });

            // Reinsert at the tail of the new (side, price) level
            var newBook = order.IsBuyOrder ? _buyBook : _sellBook;
            GetOrCreateLevel(newBook, order.Price).AddLast(idx.Node);

            // Update index metadata
            idx.IsBuy = order.IsBuyOrder;
            idx.Price = order.Price;
        }
        else if (increasedSize)
        {
            // Same (side, price); just credit the size delta.
            CreditLevelQty(order.IsBuyOrder, order.Price, order.RemainingQuantity - oldRemaining);

            // Move to tail to preserve FIFO loss for the increased portion.
            RemoveIndexEntry(new IndexEntry { IsBuy = oldIsBuy, Price = oldPrice, Node = idx.Node });
            var newBook = order.IsBuyOrder ? _buyBook : _sellBook;
            GetOrCreateLevel(newBook, order.Price).AddLast(idx.Node);
        }
        // else: no qty/side/price change → nothing to do.
    }

    // Mark the book dirty. The actual Changed event is deferred to FlushChanged so a
    // match loop that touches N makers only notifies once, not N times.
    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>
    /// Fire a single <see cref="Changed"/> notification if any mutations have occurred
    /// since the last flush. Call this after a batch of upserts/removes (typically at
    /// the end of a WithBookLockAsync body).
    /// </summary>
    public void FlushChanged()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch { /* subscriber errors must not break trading */ }
    }

    // --- Aggregate maintenance helpers ----------------------------------------
    // All callers must already hold _gate.

    private void CreditLevelQty(bool isBuy, decimal price, int qty)
    {
        if (qty <= 0) return;
        var dict = isBuy ? _buyQtyByPrice : _sellQtyByPrice;
        dict.TryGetValue(price, out var current);
        dict[price] = current + qty;
    }

    private void DebitLevelQty(bool isBuy, decimal price, int qty)
    {
        if (qty <= 0) return;
        var dict = isBuy ? _buyQtyByPrice : _sellQtyByPrice;
        if (!dict.TryGetValue(price, out var current)) return;
        var next = current - qty;
        if (next <= 0) dict.Remove(price);
        else dict[price] = next;
    }

    private void IncrementSelfCount(bool isBuy, int userId)
    {
        var dict = isBuy ? _buySelfCount : _sellSelfCount;
        dict.TryGetValue(userId, out var c);
        dict[userId] = c + 1;
    }

    private void DecrementSelfCount(bool isBuy, int userId)
    {
        var dict = isBuy ? _buySelfCount : _sellSelfCount;
        if (!dict.TryGetValue(userId, out var c)) return;
        if (c <= 1) dict.Remove(userId);
        else dict[userId] = c - 1;
    }
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

