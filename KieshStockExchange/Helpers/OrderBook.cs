using KieshStockExchange.Models;
using System.Diagnostics;

namespace KieshStockExchange.Helpers;

public sealed class OrderBook
{
    #region Properties and Constructor
    public readonly int StockId;

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

    public OrderBook(int stockId)
        => StockId = stockId;
    #endregion

    #region Order management
    /// <summary>
    /// Insert if new and if it already exists, then it will update its price/side (and position).
    /// If the order is not an Open Limit order, it is removed from the book.
    /// </summary>
    public void UpsertOrder(Order incoming)
    {
        if (incoming == null)
            throw new ArgumentNullException(nameof(incoming), "Order cannot be null.");
        if (incoming.StockId != StockId)
            throw new ArgumentException($"Order must match the book's stock ID {StockId}.", nameof(incoming));

        lock (_gate)
        {
            // If this OrderId already lives in the book, we have an entry to update or remove.
            if (_index.TryGetValue(incoming.OrderId, out var idx))
            {
                // If it’s no longer an Open Limit order, drop it from the book.
                if (!incoming.IsOpen && incoming.IsLimitOrder)
                {
                    RemoveIndexEntry(idx);
                    _index.Remove(incoming.OrderId);
                    return;
                }

                // Set the object reference
                idx.Node.Value = incoming;

                // Check if the side or the price has changed
                if (idx.IsBuy != incoming.IsBuyOrder || idx.Price != incoming.Price)
                {
                    // Remove from the old index entry
                    RemoveIndexEntry(idx);

                    // Reinsert at the tail of the new (side, price) level
                    var book = incoming.IsBuyOrder ? _buyBook : _sellBook;
                    var list = GetOrCreateLevel(book, incoming.Price);
                    list.AddLast(idx.Node);

                    // 3) Update index metadata
                    idx.IsBuy = incoming.IsBuyOrder;
                    idx.Price = incoming.Price;
                }

                return;
            }

            // If it gets here the order in not in the book.
            // But only add Open Limit orders to the book.
            if (!incoming.IsOpen && incoming.IsLimitOrder) 
                return;

            var newNode = new LinkedListNode<Order>(incoming);
            var targetBook = incoming.IsBuyOrder ? _buyBook : _sellBook;
            var level = GetOrCreateLevel(targetBook, incoming.Price);
            level.AddLast(newNode);

            _index[incoming.OrderId] = new IndexEntry
            {
                IsBuy = incoming.IsBuyOrder,
                Price = incoming.Price,
                Node = newNode
            };
        }
    }

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

    public bool RemoveById(int orderId)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(orderId, out var idx)) return false;
            RemoveIndexEntry(idx);
            _index.Remove(orderId);
            return true;
        }
    }

    public Order? RemoveBestBuy() => RemoveBest(_buyBook);
    public Order? RemoveBestSell() => RemoveBest(_sellBook);

    #endregion

    #region Helpers
    private Order? RemoveBest(SortedDictionary<decimal, LinkedList<Order>> side)
    {
        lock (_gate)
        {
            if (side.Count == 0) 
                return null;
            var kv = side.First();
            var node = kv.Value.First;
            var order = node!.Value;

            if (kv.Value.Count == 0)
                side.Remove(kv.Key);

            _index.Remove(order.OrderId);
            return order;
        }
    }

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


    #endregion
}

public sealed record PriceLevel(decimal Price, int Quantity);
public sealed record BookSnapshot
{
    public int StockId { get; init; }
    public List<PriceLevel> Buys { get; init; } = new();
    public List<PriceLevel> Sells { get; init; } = new();
}

