using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.PortfolioServices.Helpers;
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
    private readonly IOrderRegistry _registry;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<AccountsCache> _logger;

    public AccountsCache(IDataBaseService db, IOrderRegistry registry,
        IReservationLedger ledger, ILogger<AccountsCache> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
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
            var openOrdersRaw = await _db.GetOpenOrdersForUsersAsync(missing, ct).ConfigureAwait(false);
            // Route every loaded order through the registry so the canonical instance is
            // shared with OrderBookCache and the settle-path lookups. If the book has
            // already cold-loaded the same OrderId we use its instance instead.
            var openOrders = new List<Order>(openOrdersRaw.Count);
            for (int i = 0; i < openOrdersRaw.Count; i++)
                openOrders.Add(_registry.GetOrAdd(openOrdersRaw[i]));
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
            var resBefore = f.ReservedBalance;
            f.ReservedBalance = 0m; // backfilled below in EnsureLoadedAsync
            _funds[(f.UserId, f.CurrencyType)] = f;
            _ledger.LogFund(f.UserId, f.CurrencyType, null, "Hydrate:LoadFund:Clear",
                -resBefore, resBefore, 0m, f.TotalBalance, f.TotalBalance);
        }
    }

    private async Task LoadPositionsAsync(List<int> userIds, CancellationToken ct)
    {
        var positions = await _db.GetPositionsForUsersAsync(userIds, ct).ConfigureAwait(false);
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var resBefore = p.ReservedQuantity;
            p.ReservedQuantity = 0; // backfilled below in EnsureLoadedAsync
            _positions[(p.UserId, p.StockId)] = p;
            _ledger.LogPosition(p.UserId, p.StockId, null, "Hydrate:LoadPosition:Clear",
                -resBefore, resBefore, 0, p.Quantity, p.Quantity);
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
                    _ledger.LogOrder(o.UserId, o.OrderId, "Remove:Hydrate:OrphanSeller:NoPos",
                        o.CurrentBuyReservation,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                    _registry.Remove(o.OrderId);
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
                    // Seed per-order field so Σ CurrentSellReservedQty == pos.ReservedQuantity
                    // holds from t=0. TakeSellReservation is additive but each order is seen
                    // exactly once here on its load.
                    o.TakeSellReservation(remaining);
                }
                else
                {
                    if (o.IsOpen) o.Cancel();
                    ordersToCancel.Add(o);
                    _ledger.LogOrder(o.UserId, o.OrderId, "Remove:Hydrate:StaleSeller:OverReserve",
                        o.CurrentBuyReservation,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                    _registry.Remove(o.OrderId);
                    _logger.LogWarning(
                        "Cancelled stale order #{OrderId} on cache load (seller {UserId}, stock {StockId}): " +
                        "would over-reserve position (Quantity={Qty}, alreadyReserved={Res}, orderRemaining={Rem}).",
                        o.OrderId, o.UserId, o.StockId, pos.Quantity, reserved, remaining);
                }
            }
            var posResBefore = pos.ReservedQuantity;
            pos.ReservedQuantity = reserved;
            _ledger.LogPosition(pos.UserId, pos.StockId, null, "Hydrate:SeedPosition",
                reserved - posResBefore, posResBefore, pos.ReservedQuantity,
                pos.Quantity, pos.Quantity);
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
                    _ledger.LogOrder(o.UserId, o.OrderId, "Remove:Hydrate:OrphanBuyer:NoFund",
                        o.CurrentBuyReservation,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                    _registry.Remove(o.OrderId);
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
                    // Seed per-order field so Σ CurrentBuyReservation == fund.ReservedBalance
                    // holds from t=0.
                    o.TakeBuyReservation(orderReservation);
                }
                else
                {
                    if (o.IsOpen) o.Cancel();
                    ordersToCancel.Add(o);
                    _ledger.LogOrder(o.UserId, o.OrderId, "Remove:Hydrate:StaleBuyer:OverReserve",
                        o.CurrentBuyReservation,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                    _registry.Remove(o.OrderId);
                    _logger.LogWarning(
                        "Cancelled stale order #{OrderId} on cache load (buyer {UserId}, currency {Currency}): " +
                        "would over-reserve funds (TotalBalance={Bal}, alreadyReserved={Res}, orderRemaining={Rem}).",
                        o.OrderId, o.UserId, o.CurrencyType, fund.TotalBalance, reserved, orderReservation);
                }
            }
            var resBefore = fund.ReservedBalance;
            fund.ReservedBalance = reserved;
            _ledger.LogFund(fund.UserId, fund.CurrencyType, null, "Hydrate:SeedFund",
                reserved - resBefore, resBefore, fund.ReservedBalance,
                fund.TotalBalance, fund.TotalBalance);
        }
    }

    #endregion

    #region Lookups and Mutations
    public Fund? GetFund(int userId, CurrencyType ccy)
        => _funds.TryGetValue((userId, ccy), out var f) ? f : null;

    public Position? GetPosition(int userId, int stockId)
        => _positions.TryGetValue((userId, stockId), out var p) ? p : null;

    public async Task ApplyExternalFundDeltaAsync(int userId, CurrencyType ccy, decimal delta, CancellationToken ct = default)
    {
        if (delta == 0m) return;
        // Only meaningful once this user's funds are cached; otherwise the next
        // EnsureLoadedAsync cold-loads the already-updated DB row.
        if (!_funds.ContainsKey((userId, ccy))) return;

        await using var gate = await AcquireFundGateAsync(userId, ccy, ct).ConfigureAwait(false);
        if (!_funds.TryGetValue((userId, ccy), out var fund)) return;

        var totBefore = fund.TotalBalance;
        try
        {
            if (delta > 0m) fund.AddFunds(delta);
            else fund.WithdrawFunds(-delta);
        }
        catch (ArgumentException ex)
        {
            // Cache diverged from the DB value the caller validated against — drop the
            // user so the next EnsureLoadedAsync re-reads the authoritative DB balance.
            _logger.LogWarning(ex, "ApplyExternalFundDelta {Delta} for user {UserId}/{Ccy} rejected; invalidating cache.",
                delta, userId, ccy);
            _funds.TryRemove((userId, ccy), out _);
            _loadedUsers.TryRemove(userId, out _);
            return;
        }

        _ledger.LogFund(userId, ccy, null, "ApplyExternalFundDelta",
            delta, fund.ReservedBalance, fund.ReservedBalance, totBefore, fund.TotalBalance);
    }

    public void TrackNewPosition(Position pos)
    {
        if (pos is null) return;
        _positions[(pos.UserId, pos.StockId)] = pos;
    }

    public void Clear()
    {
        _funds.Clear();
        _positions.Clear();
        _loadedUsers.Clear();
        // Per-user gates are intentionally NOT cleared — disposing them while held
        // by an in-flight settle/cancel would deadlock that caller. They're cheap to
        // keep (one SemaphoreSlim per touched user); the next caller still gets
        // mutual exclusion against any survivors.
    }
    #endregion

    #region Reservation Reconciler
    public async Task<IReadOnlyList<ReservationMismatch>> ReconcileReservationsAsync(
        bool clamp = false, CancellationToken ct = default)
    {
        // Single O(N) pass over the registry: aggregate expected sums per (user, resource)
        // AND collect offenders (closed orders with non-zero CurrentReservation) by the same
        // key. Pre-fix this was O(F × N) + O(P × N) with full registry walks per fund/position;
        // at 20k users + 100k+ orders the tick loop froze for >1 min per reconcile pass.
        var expectedBalByFund = new Dictionary<(int, CurrencyType), decimal>();
        var expectedQtyByPos  = new Dictionary<(int, int), int>();
        var fundOrderCount    = new Dictionary<(int, CurrencyType), int>();
        var posOrderCount     = new Dictionary<(int, int), int>();
        Dictionary<(int, CurrencyType), List<string>>? fundOffenders = null;
        Dictionary<(int, int), List<string>>? posOffenders = null;
        // Open-order contributors per fund/position — used by the second pass to
        // emit per-order ledger rows when actual < expected (under-reserve direction).
        Dictionary<(int, CurrencyType), List<Order>>? openBuysByFund = null;
        Dictionary<(int, int), List<Order>>? openSellsByPos = null;

        foreach (var o in _registry.AllOrders())
        {
            ct.ThrowIfCancellationRequested();

            if (o.IsBuyOrder && o.CurrentBuyReservation > 0m)
            {
                var key = (o.UserId, o.CurrencyType);
                if (o.IsOpen)
                {
                    expectedBalByFund.TryGetValue(key, out var sum);
                    expectedBalByFund[key] = sum + o.CurrentBuyReservation;
                    fundOrderCount.TryGetValue(key, out var c);
                    fundOrderCount[key] = c + 1;
                    openBuysByFund ??= new();
                    if (!openBuysByFund.TryGetValue(key, out var openList))
                        openBuysByFund[key] = openList = new List<Order>();
                    openList.Add(o);
                }
                else if (o.IsClosed)
                {
                    fundOffenders ??= new();
                    if (!fundOffenders.TryGetValue(key, out var list))
                        fundOffenders[key] = list = new List<string>();
                    list.Add($"#{o.OrderId}({o.Status},amt={o.CurrentBuyReservation})");
                    // Persist offender to CSV. Two sequential reconciles tell us whether the
                    // same OrderId reappears (persistent orphan) or vanishes (transient race
                    // between Status flip and CBR release).
                    _ledger.LogOrder(o.UserId, o.OrderId, $"Reconcile:Offender:Buy:{o.Status}",
                        o.CurrentBuyReservation,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                }
            }
            else if (o.IsSellOrder && o.CurrentSellReservedQty > 0)
            {
                var key = (o.UserId, o.StockId);
                if (o.IsOpen && o.IsLimitOrder)
                {
                    expectedQtyByPos.TryGetValue(key, out var sum);
                    expectedQtyByPos[key] = sum + o.CurrentSellReservedQty;
                    posOrderCount.TryGetValue(key, out var c);
                    posOrderCount[key] = c + 1;
                    openSellsByPos ??= new();
                    if (!openSellsByPos.TryGetValue(key, out var openList))
                        openSellsByPos[key] = openList = new List<Order>();
                    openList.Add(o);
                }
                else if (o.IsClosed)
                {
                    posOffenders ??= new();
                    if (!posOffenders.TryGetValue(key, out var list))
                        posOffenders[key] = list = new List<string>();
                    list.Add($"#{o.OrderId}({o.Status},qty={o.CurrentSellReservedQty})");
                    _ledger.LogOrder(o.UserId, o.OrderId, $"Reconcile:Offender:Sell:{o.Status}",
                        0m,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                }
            }
        }

        var mismatches = new List<ReservationMismatch>();

        foreach (var kv in _positions)
        {
            ct.ThrowIfCancellationRequested();
            var actual = kv.Value.ReservedQuantity;
            if (actual == 0) continue;

            expectedQtyByPos.TryGetValue(kv.Key, out var expected);
            if (expected == actual) continue;

            posOrderCount.TryGetValue(kv.Key, out var count);
            mismatches.Add(new ReservationMismatch(
                kv.Key.UserId, kv.Key.StockId, null,
                expected, actual, actual - expected, count));

            if (posOffenders is not null
                && posOffenders.TryGetValue(kv.Key, out var offenders)
                && offenders.Count > 0)
            {
                _logger.LogWarning(
                    "Phantom position reservation user={UserId} stock={StockId} Δ={Delta}; offenders: [{Offenders}]",
                    kv.Key.UserId, kv.Key.StockId, actual - expected, string.Join(",", offenders));
            }

            // Under-reserve direction: actual < expected. Emit one ledger row per
            // open sell contributor so successive reconciles distinguish a churning
            // race (different OrderIds each pass) from persistent drift (same IDs).
            if (actual < expected
                && openSellsByPos is not null
                && openSellsByPos.TryGetValue(kv.Key, out var underSells))
            {
                for (int i = 0; i < underSells.Count; i++)
                {
                    var o = underSells[i];
                    _ledger.LogOrder(o.UserId, o.OrderId, "Reconcile:Offender:Sell:UnderReserve",
                        0m,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                }
            }
        }

        foreach (var kv in _funds)
        {
            ct.ThrowIfCancellationRequested();
            var actual = kv.Value.ReservedBalance;
            if (actual == 0m) continue;

            expectedBalByFund.TryGetValue(kv.Key, out var expected);
            if (expected == actual) continue;

            fundOrderCount.TryGetValue(kv.Key, out var count);
            mismatches.Add(new ReservationMismatch(
                kv.Key.UserId, null, kv.Key.Ccy,
                expected, actual, actual - expected, count));

            if (fundOffenders is not null
                && fundOffenders.TryGetValue(kv.Key, out var offenders)
                && offenders.Count > 0)
            {
                _logger.LogWarning(
                    "Phantom fund reservation user={UserId} ccy={Ccy} Δ={Delta}; offenders: [{Offenders}]",
                    kv.Key.UserId, kv.Key.Ccy, actual - expected, string.Join(",", offenders));
            }

            if (actual < expected
                && openBuysByFund is not null
                && openBuysByFund.TryGetValue(kv.Key, out var underBuys))
            {
                for (int i = 0; i < underBuys.Count; i++)
                {
                    var o = underBuys[i];
                    _ledger.LogOrder(o.UserId, o.OrderId, "Reconcile:Offender:Buy:UnderReserve",
                        o.CurrentBuyReservation,
                        o.CurrentBuyReservation, o.CurrentBuyReservation,
                        o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                }
            }
        }

        // Phase 2: phantom-only clamp under per-user gates. Delta > 0 means the cache
        // aggregate over-reserves vs the live open-order sum; reduce it to match. Each
        // clamp re-derives that one user's expected under their gate so the read-modify-
        // write is atomic against a concurrent settle. Under-reserve (Delta < 0) stays
        // report-only — fabricating reserved balance would be a money bug.
        if (clamp)
        {
            for (int i = 0; i < mismatches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var m = mismatches[i];
                if (m.Delta <= 0m) continue;
                if (m.StockId is int sid)        await ClampPositionAsync(m.UserId, sid, ct).ConfigureAwait(false);
                else if (m.Currency is CurrencyType ccy) await ClampFundAsync(m.UserId, ccy, ct).ConfigureAwait(false);
            }
        }

        return mismatches;
    }

    // Re-derive the user's true reserved balance from their open buys under the gate,
    // then snap the cache + DB down if it still over-reserves.
    private async Task ClampFundAsync(int userId, CurrencyType ccy, CancellationToken ct)
    {
        await using var gate = await AcquireFundGateAsync(userId, ccy, ct).ConfigureAwait(false);
        if (!_funds.TryGetValue((userId, ccy), out var fund)) return;

        decimal expected = 0m;
        foreach (var o in _registry.GetOpenBuysForUser(userId, ccy))
            if (o.IsOpen && o.CurrentBuyReservation > 0m) expected += o.CurrentBuyReservation;

        if (fund.ReservedBalance <= expected) return; // resolved or reversed since the pass
        fund.ReservedBalance = expected;
        await _db.UpdateFund(fund, ct).ConfigureAwait(false);
    }

    private async Task ClampPositionAsync(int userId, int stockId, CancellationToken ct)
    {
        await using var gate = await AcquirePositionGateAsync(userId, stockId, ct).ConfigureAwait(false);
        if (!_positions.TryGetValue((userId, stockId), out var pos)) return;

        int expected = 0;
        foreach (var o in _registry.GetOpenSellsForUser(userId, stockId))
            if (o.IsOpen && o.IsLimitOrder && o.CurrentSellReservedQty > 0) expected += o.CurrentSellReservedQty;

        if (pos.ReservedQuantity <= expected) return;
        pos.ReservedQuantity = expected;
        await _db.UpdatePosition(pos, ct).ConfigureAwait(false);
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
