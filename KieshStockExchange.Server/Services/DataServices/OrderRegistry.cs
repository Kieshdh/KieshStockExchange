using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;

namespace KieshStockExchange.Services.DataServices;

public sealed class OrderRegistry : IOrderRegistry
{
    // OrderId is the immutable primary key once assigned; ConcurrentDictionary
    // gives us thread-safe Add/Get/Remove with the writer-set semantics we need.
    private readonly ConcurrentDictionary<int, Order> _byId = new();

    public bool TryGet(int orderId, out Order order)
    {
        if (orderId > 0 && _byId.TryGetValue(orderId, out var found))
        {
            order = found;
            return true;
        }
        order = null!;
        return false;
    }

    public Order GetOrAdd(Order candidate)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (candidate.OrderId <= 0)
            throw new ArgumentException("GetOrAdd requires an assigned OrderId.", nameof(candidate));
        return _byId.GetOrAdd(candidate.OrderId, candidate);
    }

    public void Register(Order order)
    {
        if (order is null) throw new ArgumentNullException(nameof(order));
        if (order.OrderId <= 0)
            throw new ArgumentException("Register requires an assigned OrderId.", nameof(order));
        _byId[order.OrderId] = order;
    }

    public void Remove(int orderId)
    {
        if (orderId > 0) _byId.TryRemove(orderId, out _);
    }

    public IReadOnlyList<Order> GetOpenBuysForUser(int userId, CurrencyType ccy)
    {
        List<Order>? matches = null;
        foreach (var kv in _byId)
        {
            var o = kv.Value;
            if (o.UserId != userId) continue;
            if (!o.IsBuyOrder) continue;
            if (o.CurrencyType != ccy) continue;
            if (!o.IsOpen) continue;
            matches ??= new List<Order>();
            matches.Add(o);
        }
        return matches ?? (IReadOnlyList<Order>)Array.Empty<Order>();
    }

    public IReadOnlyList<Order> GetOpenSellsForUser(int userId, int stockId)
    {
        List<Order>? matches = null;
        foreach (var kv in _byId)
        {
            var o = kv.Value;
            if (o.UserId != userId) continue;
            if (!o.IsSellOrder) continue;
            if (o.StockId != stockId) continue;
            if (!o.IsOpen) continue;
            matches ??= new List<Order>();
            matches.Add(o);
        }
        return matches ?? (IReadOnlyList<Order>)Array.Empty<Order>();
    }

    // §F14: a user's open resting shorts in one currency (any stock) — limit sells carrying a
    // place-time short-collateral hold. Used by the fund reconcile/clamp to count that hold so it
    // isn't read as a phantom or clamped away.
    public IReadOnlyList<Order> GetOpenShortSellsForUser(int userId, CurrencyType ccy)
    {
        List<Order>? matches = null;
        foreach (var kv in _byId)
        {
            var o = kv.Value;
            if (o.UserId != userId) continue;
            if (!o.IsSellOrder) continue;
            if (o.CurrencyType != ccy) continue;
            if (!o.IsOpen) continue;
            if (o.CurrentShortCollateral <= 0m) continue;
            matches ??= new List<Order>();
            matches.Add(o);
        }
        return matches ?? (IReadOnlyList<Order>)Array.Empty<Order>();
    }

    public IReadOnlyList<Order> GetArmedSellStopsForUser(int userId, int stockId)
    {
        List<Order>? matches = null;
        foreach (var kv in _byId)
        {
            var o = kv.Value;
            if (o.UserId != userId) continue;
            if (!o.IsSellOrder) continue;
            if (o.StockId != stockId) continue;
            if (!o.IsArmed || !o.IsStopOrder) continue;
            matches ??= new List<Order>();
            matches.Add(o);
        }
        return matches ?? (IReadOnlyList<Order>)Array.Empty<Order>();
    }

    public IReadOnlyList<Order> GetArmedBuyStopsForUser(int userId, CurrencyType ccy)
    {
        List<Order>? matches = null;
        foreach (var kv in _byId)
        {
            var o = kv.Value;
            if (o.UserId != userId) continue;
            if (!o.IsBuyOrder) continue;
            if (o.CurrencyType != ccy) continue;
            if (!o.IsArmed || !o.IsStopOrder) continue;
            matches ??= new List<Order>();
            matches.Add(o);
        }
        return matches ?? (IReadOnlyList<Order>)Array.Empty<Order>();
    }

    public IEnumerable<Order> AllOrders()
    {
        // ConcurrentDictionary's enumerator is safe but doesn't observe a consistent
        // snapshot — that's fine for the reconciler's diagnostic walk.
        foreach (var kv in _byId)
            yield return kv.Value;
    }

    public int Count => _byId.Count;

    public void Clear() => _byId.Clear();
}
