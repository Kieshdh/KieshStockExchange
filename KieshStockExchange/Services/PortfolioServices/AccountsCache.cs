using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketEngineServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.PortfolioServices;

public sealed class AccountsCache : IAccountsCache
{
    #region Private State
    private readonly ConcurrentDictionary<(int UserId, CurrencyType Ccy), Fund> _funds = new();
    private readonly ConcurrentDictionary<(int UserId, int StockId), Position> _positions = new();
    private readonly ConcurrentDictionary<int, byte> _loadedUsers = new();
    
    // Single gate around the cold-load section so we don't issue duplicate DB reads
    // when many parallel callers ask to load the same user. Hot-path lookups don't
    // touch this gate.
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    #endregion

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly ILogger<AccountsCache> _logger;

    public AccountsCache(IDataBaseService db, ILogger<AccountsCache> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Loading
    public Task EnsureLoadedAsync(int userId, CancellationToken ct = default)
        => EnsureLoadedAsync(new[] { userId }, ct);

    public async Task EnsureLoadedAsync(IReadOnlyList<int> userIds, CancellationToken ct = default)
    {
        var missing = CollectMissingUsers(userIds);
        if (missing is null) return;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another caller may have loaded these in the meantime.
            for (int i = missing.Count - 1; i >= 0; i--)
                if (_loadedUsers.ContainsKey(missing[i])) missing.RemoveAt(i);
            if (missing.Count == 0) return;

            await LoadFundsAsync(missing, ct).ConfigureAwait(false);
            await LoadPositionsAsync(missing, ct).ConfigureAwait(false);

            // Backfill ReservedQuantity (sells) and ReservedBalance (buys) from open orders,
            // clamped to actual Position.Quantity / Fund.TotalBalance. Cancels orders whose
            // cumulative reservation would exceed the backing resource (oldest-first wins,
            // matching the order book's price-time priority intuition). Catches the stale-
            // order class produced by xlsx reseeds that drop positions/funds without
            // dropping the corresponding orders.
            var openOrders = await _db.GetOpenOrdersForUsersAsync(missing, ct).ConfigureAwait(false);
            var (sellsByPos, buysByFund) = GroupOpenOrdersBySide(openOrders);

            var ordersToCancel = new List<Order>();
            ClampSellsToPositionQuantity(sellsByPos, ordersToCancel);
            ClampBuysToFundBalance(buysByFund, ordersToCancel);

            if (ordersToCancel.Count > 0)
                await _db.UpdateAllAsync(ordersToCancel, ct).ConfigureAwait(false);

            // Mark all requested users as loaded — even if they had no rows, so we don't
            // re-query the DB for empty results.
            for (int i = 0; i < missing.Count; i++)
                _loadedUsers[missing[i]] = 0;
        }
        finally { _loadGate.Release(); }
    }

    private List<int>? CollectMissingUsers(IReadOnlyList<int> userIds)
    {
        if (userIds is null || userIds.Count == 0) return null;
        List<int>? missing = null;
        for (int i = 0; i < userIds.Count; i++)
        {
            if (!_loadedUsers.ContainsKey(userIds[i]))
            {
                missing ??= new List<int>();
                missing.Add(userIds[i]);
            }
        }
        return missing;
    }

    private async Task LoadFundsAsync(List<int> userIds, CancellationToken ct)
    {
        var funds = await _db.GetFundsForUsersAsync(userIds, ct).ConfigureAwait(false);
        for (int i = 0; i < funds.Count; i++)
        {
            var f = funds[i];
            f.ReservedBalance = 0m; // backfilled below in EnsureLoadedAsync
            _funds[(f.UserId, f.CurrencyType)] = f;
        }
    }

    private async Task LoadPositionsAsync(List<int> userIds, CancellationToken ct)
    {
        var positions = await _db.GetPositionsForUsersAsync(userIds, ct).ConfigureAwait(false);
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            p.ReservedQuantity = 0; // backfilled below in EnsureLoadedAsync
            _positions[(p.UserId, p.StockId)] = p;
        }
    }

    private static (Dictionary<(int, int), List<Order>> Sells,
                    Dictionary<(int, CurrencyType), List<Order>> Buys)
        GroupOpenOrdersBySide(IReadOnlyList<Order> openOrders)
    {
        var sellsByPos = new Dictionary<(int, int), List<Order>>();
        var buysByFund = new Dictionary<(int, CurrencyType), List<Order>>();
        for (int i = 0; i < openOrders.Count; i++)
        {
            var o = openOrders[i];
            if (o.RemainingQuantity <= 0) continue;
            if (o.IsSellOrder)
            {
                var key = (o.UserId, o.StockId);
                if (!sellsByPos.TryGetValue(key, out var list))
                    sellsByPos[key] = list = new List<Order>();
                list.Add(o);
            }
            else if (o.IsBuyOrder)
            {
                var key = (o.UserId, o.CurrencyType);
                if (!buysByFund.TryGetValue(key, out var list))
                    buysByFund[key] = list = new List<Order>();
                list.Add(o);
            }
        }
        return (sellsByPos, buysByFund);
    }

    private void ClampSellsToPositionQuantity(
        Dictionary<(int, int), List<Order>> sellsByPos,
        List<Order> ordersToCancel)
    {
        foreach (var kv in sellsByPos)
        {
            var list = kv.Value;
            list.Sort(static (a, b) => a.OrderId.CompareTo(b.OrderId));

            if (!_positions.TryGetValue(kv.Key, out var pos))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (o.IsOpen) o.Cancel();
                    ordersToCancel.Add(o);
                    _logger.LogWarning(
                        "Cancelled orphan order #{OrderId} on cache load (seller {UserId}, stock {StockId}): no Position row.",
                        o.OrderId, o.UserId, o.StockId);
                }
                continue;
            }

            int reserved = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                var remaining = o.RemainingQuantity;
                if (reserved + remaining <= pos.Quantity)
                {
                    reserved += remaining;
                }
                else
                {
                    if (o.IsOpen) o.Cancel();
                    ordersToCancel.Add(o);
                    _logger.LogWarning(
                        "Cancelled stale order #{OrderId} on cache load (seller {UserId}, stock {StockId}): " +
                        "would over-reserve position (Quantity={Qty}, alreadyReserved={Res}, orderRemaining={Rem}).",
                        o.OrderId, o.UserId, o.StockId, pos.Quantity, reserved, remaining);
                }
            }
            pos.ReservedQuantity = reserved;
        }
    }

    private void ClampBuysToFundBalance(
        Dictionary<(int, CurrencyType), List<Order>> buysByFund,
        List<Order> ordersToCancel)
    {
        foreach (var kv in buysByFund)
        {
            var list = kv.Value;
            list.Sort(static (a, b) => a.OrderId.CompareTo(b.OrderId));

            if (!_funds.TryGetValue(kv.Key, out var fund))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (o.IsOpen) o.Cancel();
                    ordersToCancel.Add(o);
                    _logger.LogWarning(
                        "Cancelled orphan order #{OrderId} on cache load (buyer {UserId}, currency {Currency}): no Fund row.",
                        o.OrderId, o.UserId, o.CurrencyType);
                }
                continue;
            }

            decimal reserved = 0m;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                var orderReservation = SettlementEngine.RemainingBuyReservation(o);
                if (orderReservation <= 0m) continue;

                if (reserved + orderReservation <= fund.TotalBalance)
                {
                    reserved += orderReservation;
                }
                else
                {
                    if (o.IsOpen) o.Cancel();
                    ordersToCancel.Add(o);
                    _logger.LogWarning(
                        "Cancelled stale order #{OrderId} on cache load (buyer {UserId}, currency {Currency}): " +
                        "would over-reserve funds (TotalBalance={Bal}, alreadyReserved={Res}, orderRemaining={Rem}).",
                        o.OrderId, o.UserId, o.CurrencyType, fund.TotalBalance, reserved, orderReservation);
                }
            }
            fund.ReservedBalance = reserved;
        }
    }

    #endregion

    #region Lookups and Mutations
    public Fund? GetFund(int userId, CurrencyType ccy)
        => _funds.TryGetValue((userId, ccy), out var f) ? f : null;

    public Position? GetPosition(int userId, int stockId)
        => _positions.TryGetValue((userId, stockId), out var p) ? p : null;

    public void TrackNewPosition(Position pos)
    {
        if (pos is null) return;
        _positions[(pos.UserId, pos.StockId)] = pos;
    }
    #endregion
}
