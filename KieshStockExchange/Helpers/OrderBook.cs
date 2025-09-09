using KieshStockExchange.Models;

namespace KieshStockExchange.Helpers;

public class OrderBook
{
    #region Properties
    private int StockId { get; }

    // Buy side: highest price first (so we invert the default comparer)
    private readonly SortedDictionary<decimal, Queue<Order>> _buyBook
        = new SortedDictionary<decimal, Queue<Order>>(
            Comparer<decimal>.Create((a, b) => b.CompareTo(a))
        );

    // Sell side: lowest price first (default ascending order)
    private readonly SortedDictionary<decimal, Queue<Order>> _sellBook
        = new SortedDictionary<decimal, Queue<Order>>();
    #endregion

    public OrderBook(int stockId)
        => StockId = stockId;

    #region Order management
    /// <summary>
    /// Adds a new limit order into the appropriate side of the book.
    /// Throws if it isn’t a limit order.
    /// </summary>
    public void AddLimitOrder(Order order)
    {
        if (order == null)
            throw new ArgumentNullException(nameof(order), "Order cannot be null.");
        if (order.StockId != StockId)
            throw new ArgumentException($"Order must match the book's stock ID {StockId}.", nameof(order));
        if (!order.IsLimitOrder())
            throw new ArgumentException("Only limit orders can be added to the book.", nameof(order));

        // Choose buy or sell book
        var book = order.IsBuyOrder() ? _buyBook : _sellBook;

        // If this price level doesn’t exist yet, create its FIFO queue
        if (!book.TryGetValue(order.Price, out var queue))
        {
            queue = new Queue<Order>();
            book[order.Price] = queue;
        }
        // Check for existing order with same ID
        if (queue.Any(o => o.OrderId == order.OrderId))
            return; // Ignore duplicates

        // Enqueue—this preserves time priority within the same price
        queue.Enqueue(order);
    }

    /// <summary>
    /// Peeks or removes the best order on the buy side.
    /// </summary>
    public Order? PeekBestBuy()
    {
        if (_buyBook.Count == 0) return null;
        var bestPrice = _buyBook.First().Key;
        return _buyBook[bestPrice].Peek();
    }

    public Order? RemoveBestBuy()
    {
        if (_buyBook.Count == 0) return null;
        var kv = _buyBook.First();
        var order = kv.Value.Dequeue();
        if (kv.Value.Count == 0)
            _buyBook.Remove(kv.Key);
        return order;
    }

    /// <summary>
    /// Peeks or removes the best order on the sell side.
    /// </summary>
    public Order? PeekBestSell()
    {
        if (_sellBook.Count == 0) return null;
        var bestPrice = _sellBook.First().Key;
        return _sellBook[bestPrice].Peek();
    }

    public Order? RemoveBestSell()
    {
        if (_sellBook.Count == 0) return null;
        var kv = _sellBook.First();
        var order = kv.Value.Dequeue();
        if (kv.Value.Count == 0)
            _sellBook.Remove(kv.Key);
        return order;
    }
    #endregion

    #region Getters for book state
    /// <summary>
    /// Enumerates all live orders on each side in execution priority.
    /// </summary>
    public IEnumerable<Order> AllBuyOrders() => 
        _buyBook.SelectMany(k => k.Value);
    public IEnumerable<Order> AllSellOrders() => 
        _sellBook.SelectMany(k => k.Value);
    #endregion
}

