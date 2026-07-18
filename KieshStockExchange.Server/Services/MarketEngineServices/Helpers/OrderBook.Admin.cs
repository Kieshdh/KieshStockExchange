using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed partial class OrderBook
{
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
}
