using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.PortfolioServices.Interfaces;

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

    // Per-user reservation gates. Before the batch path's _writeGate restructure (Section 1b)
    // every fund/position mutation was serialised by SQLite's writer; once the batch gives
    // up its single root tx, two flows touching the same (user, currency) or (user, stockId)
    // can race on ReserveFunds/ReserveStock/UnreserveStock. These semaphores serialise the
    // mutation step at the (user, resource) granularity — far more concurrency than the
    // global writer gate, but enough to prevent the lost-update bug on ReservedBalance /
    // ReservedQuantity. Gates are created lazily and live for the process lifetime.
    private readonly ConcurrentDictionary<(int UserId, CurrencyType Ccy), SemaphoreSlim> _fundGates = new();
    private readonly ConcurrentDictionary<(int UserId, int StockId), SemaphoreSlim> _posGates = new();
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
                var orderReservation = ReservationMath.RemainingBuyReservation(o);
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

    #region Reservation Reconciler
    public async Task<IReadOnlyList<ReservationMismatch>> ReconcileReservationsAsync(
        bool clamp = false, CancellationToken ct = default)
    {
        // Collect every userId that has a non-zero reservation in either dictionary.
        // Skip the whole DB query if there's nothing to reconcile.
        var userIds = new HashSet<int>();
        foreach (var kv in _positions)
            if (kv.Value.ReservedQuantity > 0) userIds.Add(kv.Key.UserId);
        foreach (var kv in _funds)
            if (kv.Value.ReservedBalance > 0m) userIds.Add(kv.Key.UserId);
        if (userIds.Count == 0) return Array.Empty<ReservationMismatch>();

        var userList = new List<int>(userIds);
        var openOrders = await _db.GetOpenOrdersForUsersAsync(userList, ct).ConfigureAwait(false);

        // Aggregate the open orders by the keys we reserve against.
        var expectedQtyByPos = new Dictionary<(int UserId, int StockId), int>();
        var expectedBalByFund = new Dictionary<(int UserId, CurrencyType Ccy), decimal>();
        var orderCountByPos = new Dictionary<(int, int), int>();
        var orderCountByFund = new Dictionary<(int, CurrencyType), int>();

        for (int i = 0; i < openOrders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var o = openOrders[i];
            if (!o.IsLimitOrder) continue;

            if (o.IsSellOrder)
            {
                var key = (o.UserId, o.StockId);
                expectedQtyByPos.TryGetValue(key, out var qSum);
                expectedQtyByPos[key] = qSum + o.RemainingQuantity;
                orderCountByPos.TryGetValue(key, out var c);
                orderCountByPos[key] = c + 1;
            }
            else if (o.IsBuyOrder)
            {
                var reservation = ReservationMath.RemainingBuyReservation(o);
                if (reservation <= 0m) continue;
                var key = (o.UserId, o.CurrencyType);
                expectedBalByFund.TryGetValue(key, out var bSum);
                expectedBalByFund[key] = bSum + reservation;
                orderCountByFund.TryGetValue(key, out var c);
                orderCountByFund[key] = c + 1;
            }
        }

        var mismatches = new List<ReservationMismatch>();

        foreach (var kv in _positions)
        {
            var actual = kv.Value.ReservedQuantity;
            if (actual == 0) continue;
            expectedQtyByPos.TryGetValue(kv.Key, out var expected);
            if (expected == actual) continue;
            orderCountByPos.TryGetValue(kv.Key, out var count);
            mismatches.Add(new ReservationMismatch(
                kv.Key.UserId, kv.Key.StockId, null,
                expected, actual, actual - expected, count));
            if (clamp) kv.Value.ReservedQuantity = expected;
        }

        foreach (var kv in _funds)
        {
            var actual = kv.Value.ReservedBalance;
            if (actual == 0m) continue;
            expectedBalByFund.TryGetValue(kv.Key, out var expected);
            if (expected == actual) continue;
            orderCountByFund.TryGetValue(kv.Key, out var count);
            mismatches.Add(new ReservationMismatch(
                kv.Key.UserId, null, kv.Key.Ccy,
                expected, actual, actual - expected, count));
            if (clamp) kv.Value.ReservedBalance = expected;
        }

        return mismatches;
    }
    #endregion

    #region Per-User Gates
    public async ValueTask<IAsyncDisposable> AcquireFundGateAsync(
        int userId, CurrencyType ccy, CancellationToken ct = default)
    {
        var gate = _fundGates.GetOrAdd((userId, ccy), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreRelease(gate);
    }

    public async ValueTask<IAsyncDisposable> AcquirePositionGateAsync(
        int userId, int stockId, CancellationToken ct = default)
    {
        var gate = _posGates.GetOrAdd((userId, stockId), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreRelease(gate);
    }

    public async ValueTask<IAsyncDisposable> AcquireUserGatesAsync(
        IReadOnlyCollection<(int UserId, CurrencyType Ccy)> fundKeys,
        IReadOnlyCollection<(int UserId, int StockId)> positionKeys,
        CancellationToken ct = default)
    {
        // Acquire in a globally-deterministic order so concurrent callers can never form
        // an AB/BA deadlock on overlapping user sets. We materialise to arrays and sort
        // up front: funds first (ordered by userId then currency byte), then positions
        // (ordered by userId then stockId). Any two batches that touch the same gates
        // hit them in this same sequence.
        var fundArr = fundKeys is { Count: > 0 }
            ? fundKeys.Distinct().OrderBy(k => k.UserId).ThenBy(k => (int)k.Ccy).ToArray()
            : Array.Empty<(int, CurrencyType)>();
        var posArr = positionKeys is { Count: > 0 }
            ? positionKeys.Distinct().OrderBy(k => k.UserId).ThenBy(k => k.StockId).ToArray()
            : Array.Empty<(int, int)>();

        var acquired = new List<SemaphoreSlim>(fundArr.Length + posArr.Length);
        try
        {
            for (int i = 0; i < fundArr.Length; i++)
            {
                var gate = _fundGates.GetOrAdd(fundArr[i], static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired.Add(gate);
            }
            for (int i = 0; i < posArr.Length; i++)
            {
                var gate = _posGates.GetOrAdd(posArr[i], static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                acquired.Add(gate);
            }
            return new MultiSemaphoreRelease(acquired);
        }
        catch
        {
            // If we threw partway, release whatever we already acquired so callers
            // aren't holding gates they never returned from.
            for (int i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
            throw;
        }
    }

    private sealed class SemaphoreRelease : IAsyncDisposable
    {
        private SemaphoreSlim? _gate;
        public SemaphoreRelease(SemaphoreSlim gate) => _gate = gate;
        public ValueTask DisposeAsync()
        {
            var g = Interlocked.Exchange(ref _gate, null);
            g?.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MultiSemaphoreRelease : IAsyncDisposable
    {
        private List<SemaphoreSlim>? _gates;
        public MultiSemaphoreRelease(List<SemaphoreSlim> gates) => _gates = gates;
        public ValueTask DisposeAsync()
        {
            var gates = Interlocked.Exchange(ref _gates, null);
            if (gates is null) return ValueTask.CompletedTask;
            // Release in reverse acquisition order — symmetric with the nested
            // semantics of a stack of using-blocks.
            for (int i = gates.Count - 1; i >= 0; i--) gates[i].Release();
            return ValueTask.CompletedTask;
        }
    }
    #endregion
}
