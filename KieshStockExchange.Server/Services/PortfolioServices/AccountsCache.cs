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

            // Cold-load reservation rebuild — ORDER MATTERS (see each method):
            //   1. ReseedBracketReservations — bracket pools onto the Position first, so ClampSells
            //      sees them as an existing baseline rather than rival sells it would cancel (Q1).
            //   2. ClampSells — non-bracket sells reserve from the REMAINING position pool; seeds
            //      covered shares for resting shorts (their collateral is seeded in step 4).
            //   3/4. Backfill*ShortCollateral — filled + resting short collateral onto the Fund.
            //   5. ClampBuys — LAST, so it caps buys against TotalBalance − collateral already held
            //      and adds (not overwrites) ReservedBalance (Q2).
            var ordersToCancel = new List<Order>();
            ReseedBracketReservations(sellsByPos, ordersToCancel);
            // §P5b: short-bracket cash pools (cash twin of ReseedBracketReservations) onto the Fund before
            // ClampBuys, which then skips bracket children rather than mis-reserving the buy TPs.
            ReseedBracketCashPools(buysByFund, ordersToCancel);
            ClampSellsToPositionQuantity(sellsByPos, ordersToCancel);

            // §3.6 P1 (risk #5): short collateral is intrinsic to the position (loaded from DB, not
            // rebuilt from open orders). Mirror each just-loaded short's ShortCollateral back into its
            // currency Fund so hydration reproduces the lock the live session held.
            BackfillShortCollateral(missing);
            // §F14: re-seed resting-short collateral. Both collateral passes run BEFORE ClampBuys so its
            // budget accounts for them (Q2) and its += doesn't clobber them.
            BackfillRestingShortCollateral(openOrders, ordersToCancel);

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

    // §P4 Q1: rebuild an ACTIVE bracket's share reservation on cold-load BEFORE the generic ClampSells.
    // An active bracket's legs load as ordinary sells (Open limit TPs + a Pending stop SL) and the generic
    // clamp — which treats each sell as an independent competitor for the position — would seed the SL's
    // whole pool and then cancel every TP as an over-reserver, silently destroying the user's take-profits
    // (conservation still holds, so the reconciler can't catch it). This pass reproduces the live arming
    // model from BracketCoordinator.OnParentFillAsync: SL present ⇒ Model B (the SL owns the whole held
    // pool = its RemainingQuantity, kept in lock-step by the coordinator; each TP reserves 0); no SL ⇒
    // take-profit-only fork (each TP reserves its own remaining shares). Runs single-threaded under
    // _loadGate; mirrors the BackfillShortCollateral "structured reservation reseeded separately" pattern.
    private void ReseedBracketReservations(
        Dictionary<(int, int), List<Order>> sellsByPos,
        List<Order> ordersToCancel)
    {
        // Group loaded bracket children by parent (a position may host several brackets + plain sells).
        Dictionary<int, List<Order>>? byParent = null;
        foreach (var kv in sellsByPos)
            for (int i = 0; i < kv.Value.Count; i++)
            {
                var o = kv.Value[i];
                if (!o.IsBracketChild || o.ParentOrderId is not int pid) continue;
                byParent ??= new Dictionary<int, List<Order>>();
                if (!byParent.TryGetValue(pid, out var g)) byParent[pid] = g = new List<Order>();
                g.Add(o);
            }
        if (byParent is null) return;

        foreach (var kv in byParent)
        {
            var group = kv.Value;
            Order? sl = null;
            var tps = new List<Order>(group.Count);
            for (int i = 0; i < group.Count; i++)
            {
                var o = group[i];
                if (o.IsStopOrder) sl = o;
                else if (o.IsLimitOrder) tps.Add(o);
            }
            // All children of one parent share a single position (the parent's stock).
            if (!_positions.TryGetValue((group[0].UserId, group[0].StockId), out var pos)) continue;

            if (sl is not null)
            {
                // Model B: SL owns the whole pool; TPs reserve nothing.
                int pool = sl.RemainingQuantity;
                if (pool <= 0) continue;
                if (pos.AvailableQuantity < pool)
                {
                    // Malformed (pool exceeds free shares) — fall back to cancelling the whole bracket's
                    // legs rather than over-reserve the position.
                    CancelBracketGroupOverReserve(kv.Key, sl, tps, ordersToCancel);
                    continue;
                }
                int before = pos.ReservedQuantity;
                pos.ReserveStock(pool);
                sl.TakeSellReservation(pool);
                _ledger.LogPosition(pos.UserId, pos.StockId, sl.OrderId, "Hydrate:BracketSLPool",
                    pool, before, pos.ReservedQuantity, pos.Quantity, pos.Quantity);
            }
            else
            {
                // Take-profit-only fork: each TP reserves its own remaining shares, nearest-market-first
                // (lowest price), exactly as OnParentFillAsync arms them.
                tps.Sort(static (a, b) => a.Price.CompareTo(b.Price));
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i];
                    int need = tp.RemainingQuantity;
                    if (need <= 0) continue;
                    if (pos.AvailableQuantity < need) need = pos.AvailableQuantity; // defensive cap
                    if (need <= 0) continue;
                    int before = pos.ReservedQuantity;
                    pos.ReserveStock(need);
                    tp.TakeSellReservation(need);
                    _ledger.LogPosition(pos.UserId, pos.StockId, tp.OrderId, "Hydrate:BracketTP",
                        need, before, pos.ReservedQuantity, pos.Quantity, pos.Quantity);
                }
            }
        }
    }

    // Defensive: a bracket whose pool exceeds its position's free shares (only reachable from an
    // inconsistent DB seed) — cancel its legs instead of over-reserving.
    private void CancelBracketGroupOverReserve(int parentId, Order? sl, List<Order> tps, List<Order> ordersToCancel)
    {
        void Cancel(Order o)
        {
            if (o.IsOpen || o.IsArmed || o.IsAttached) o.Cancel();
            ordersToCancel.Add(o);
            _ledger.LogOrder(o.UserId, o.OrderId, "Remove:Hydrate:BracketOverReserve",
                o.CurrentBuyReservation, o.CurrentBuyReservation, o.CurrentBuyReservation,
                o.CurrentSellReservedQty, o.CurrentSellReservedQty);
            _registry.Remove(o.OrderId);
        }
        if (sl is not null) Cancel(sl);
        for (int i = 0; i < tps.Count; i++) Cancel(tps[i]);
        _logger.LogWarning(
            "Cancelled malformed bracket #{Parent} legs on cache load: pooled reservation exceeds position.",
            parentId);
    }

    // §P5b: rebuild an active SHORT bracket's cash reservations on cold-load — the fund-side twin of
    // ReseedBracketReservations. A short bracket's legs load as BUYS: a Pending buy-stop SL owning the cash
    // pool (SL_worst × held), and Open buy-limit TPs that reserve 0 (drawing the pool) — or, for a
    // take-profit-only short, each TP reserving its own buyback. The generic ClampBuys would mis-reserve the
    // buy TPs, so seed the right cash here and have ClampBuys skip bracket children. Runs single-threaded
    // under _loadGate; direct fund.ReservedBalance += like BackfillShortCollateral.
    private void ReseedBracketCashPools(
        Dictionary<(int, CurrencyType), List<Order>> buysByFund, List<Order> ordersToCancel)
    {
        Dictionary<int, List<Order>>? byParent = null;
        foreach (var kv in buysByFund)
            for (int i = 0; i < kv.Value.Count; i++)
            {
                var o = kv.Value[i];
                if (!o.IsBracketChild || o.ParentOrderId is not int pid) continue;
                byParent ??= new Dictionary<int, List<Order>>();
                if (!byParent.TryGetValue(pid, out var g)) byParent[pid] = g = new List<Order>();
                g.Add(o);
            }
        if (byParent is null) return;

        foreach (var kv in byParent)
        {
            var group = kv.Value;
            Order? sl = null;
            var tps = new List<Order>(group.Count);
            for (int i = 0; i < group.Count; i++)
            {
                var o = group[i];
                if (o.IsStopOrder) sl = o;
                else if (o.IsLimitOrder) tps.Add(o);
            }
            if (!_funds.TryGetValue((group[0].UserId, group[0].CurrencyType), out var fund)) continue;

            if (sl is not null)
            {
                // Coordinator persisted sl.Quantity = held + AmountFilled, so RemainingQuantity == held.
                int held = sl.RemainingQuantity;
                if (held <= 0) continue;
                decimal pool = ShortBracketMath.Pool(
                    ShortBracketMath.SlWorst(sl.IsStopLimitOrder, sl.Price, sl.StopPrice ?? 0m, sl.SlippagePercent ?? 0m),
                    held);
                if (pool <= 0m) continue;
                var resBefore = fund.ReservedBalance;
                fund.ReservedBalance += pool;
                sl.TakeBuyReservation(pool);
                _ledger.LogFund(sl.UserId, sl.CurrencyType, sl.OrderId, "Hydrate:BracketSLCashPool",
                    pool, resBefore, fund.ReservedBalance, fund.TotalBalance, fund.TotalBalance);
                // TPs reserve 0 (they draw the SL pool) — nothing to seed.
            }
            else
            {
                // Take-profit-only short: each TP reserves its own remaining buyback.
                for (int i = 0; i < tps.Count; i++)
                {
                    var tp = tps[i];
                    int rem = tp.RemainingQuantity;
                    if (rem <= 0) continue;
                    decimal need = CurrencyHelper.Notional(tp.Price, rem, tp.CurrencyType);
                    if (need <= 0m) continue;
                    var resBefore = fund.ReservedBalance;
                    fund.ReservedBalance += need;
                    tp.TakeBuyReservation(need);
                    _ledger.LogFund(tp.UserId, tp.CurrencyType, tp.OrderId, "Hydrate:BracketTPCash",
                        need, resBefore, fund.ReservedBalance, fund.TotalBalance, fund.TotalBalance);
                }
            }
        }
    }

    private void ClampSellsToPositionQuantity(
        Dictionary<(int, int), List<Order>> sellsByPos,
        List<Order> ordersToCancel)
    {
        foreach (var kv in sellsByPos)
        {
            var list = kv.Value;
            list.Sort(static (a, b) => a.OrderId.CompareTo(b.OrderId));

            // pos may be null (a flat seller with only resting shorts) — posQty 0 covers nothing.
            _positions.TryGetValue(kv.Key, out var pos);
            int posQty = pos?.Quantity ?? 0;
            // §P4 Q1: start from the bracket pool ReseedBracketReservations already put on the position,
            // so non-bracket sells compete only for the REMAINING shares (and the final assignment below
            // keeps that pool instead of overwriting it). 0 for any position without an active bracket.
            int reserved = pos?.ReservedQuantity ?? 0;

            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                // §P4 Q1: bracket legs are reserved by ReseedBracketReservations (SL owns the pool / TPs
                // reserve their own), never by the generic per-sell clamp — it would cancel pooled TPs.
                if (o.IsBracketChild) continue;
                var remaining = o.RemainingQuantity;

                // §F14: a LIMIT sell rests partly/fully as a short. Seed ONLY the covered shares here
                // (what the position can still back, oldest-first) into the position pool. The uncovered
                // remainder's cash collateral is re-seeded later in BackfillRestingShortCollateral —
                // AFTER ClampBuysToFundBalance ASSIGNS fund.ReservedBalance, which would otherwise clobber
                // a fund bump done here. A resting short is never cancelled for lack of shares.
                if (o.IsLimitOrder)
                {
                    // Seed per-order field so Σ CurrentSellReservedQty == pos.ReservedQuantity holds
                    // from t=0. TakeSellReservation is additive but each order is seen exactly once.
                    int covered = Math.Max(0, Math.Min(remaining, posQty - reserved));
                    if (covered > 0) { o.TakeSellReservation(covered); reserved += covered; }
                    continue;
                }

                // Legacy non-limit sell: must be fully share-backed or it's stale.
                if (pos is null)
                {
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
                    continue;
                }
                if (reserved + remaining <= pos.Quantity)
                {
                    reserved += remaining;
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

            if (pos is not null)
            {
                var posResBefore = pos.ReservedQuantity;
                pos.ReservedQuantity = reserved;
                _ledger.LogPosition(pos.UserId, pos.StockId, null, "Hydrate:SeedPosition",
                    reserved - posResBefore, posResBefore, pos.ReservedQuantity,
                    pos.Quantity, pos.Quantity);
            }
        }
    }

    // §F14: re-seed each resting short's place-time cash collateral on cold-load (the order's
    // CurrentShortCollateral is in-memory only, lost across restart). Mirrors BackfillShortCollateral
    // but for the UNFILLED short remainder held on the order, not the filled Position.ShortCollateral.
    // MUST run AFTER ClampBuysToFundBalance: that pass ASSIGNS fund.ReservedBalance, so a += here is the
    // only way the hold survives. After ClampSells, a limit sell's CurrentSellReservedQty == its covered
    // shares, so the uncovered remainder (RemainingQuantity − covered) is the short part. A short with no
    // Fund row to back it is a genuine orphan: release its covered shares and cancel it.
    private void BackfillRestingShortCollateral(IReadOnlyList<Order> openOrders, List<Order> ordersToCancel)
    {
        for (int i = 0; i < openOrders.Count; i++)
        {
            var o = openOrders[i];
            if (!o.IsSellOrder || !o.IsLimitOrder || !o.IsOpen) continue;
            // A bracket TP is an Open limit sell whose shares are pooled on its sibling SL (it may carry
            // CurrentSellReservedQty == 0), NOT a resting short — skip it or we'd seed phantom collateral.
            if (o.IsBracketChild) continue;
            int shortQty = o.RemainingQuantity - o.CurrentSellReservedQty;
            if (shortQty <= 0) continue; // fully share-covered long sell

            if (!_funds.TryGetValue((o.UserId, o.CurrencyType), out var fund))
            {
                // No fund to back the short → orphan. Release the covered shares ClampSells seeded.
                var covered = o.CurrentSellReservedQty;
                if (covered > 0 && _positions.TryGetValue((o.UserId, o.StockId), out var pos))
                {
                    pos.ReservedQuantity = Math.Max(0, pos.ReservedQuantity - covered);
                    o.ConsumeSellReservation(covered);
                }
                o.Cancel();
                ordersToCancel.Add(o);
                _ledger.LogOrder(o.UserId, o.OrderId, "Remove:Hydrate:OrphanShort:NoFund",
                    o.CurrentBuyReservation, o.CurrentBuyReservation, o.CurrentBuyReservation,
                    o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                _registry.Remove(o.OrderId);
                _logger.LogWarning(
                    "Cancelled resting short #{OrderId} on cache load (seller {UserId}, stock {StockId}): " +
                    "no Fund row in {Ccy} to back {ShortQty} short share(s).",
                    o.OrderId, o.UserId, o.StockId, o.CurrencyType, shortQty);
                continue;
            }

            var collateral = ReservationMath.ShortCollateralForResting(o, shortQty);
            if (collateral <= 0m) continue; // degenerate (zero price) — nothing to hold
            var resBefore = fund.ReservedBalance;
            fund.ReservedBalance += collateral;
            o.TakeShortCollateral(collateral);
            _ledger.LogFund(o.UserId, o.CurrencyType, o.OrderId, "Hydrate:RestingShortCollateral",
                collateral, resBefore, fund.ReservedBalance, fund.TotalBalance, fund.TotalBalance);
        }
    }

    // §3.6 P1 (risk #5): add each short position's collateral into its currency Fund's
    // ReservedBalance for the just-loaded users. Scoped to `userIds` so a later partial
    // load never double-counts collateral already mirrored on a prior load.
    private void BackfillShortCollateral(List<int> userIds)
    {
        var set = new HashSet<int>(userIds);
        foreach (var kv in _positions)
        {
            var pos = kv.Value;
            if (!set.Contains(pos.UserId) || pos.ShortCollateral <= 0m) continue;
            if (!_funds.TryGetValue((pos.UserId, pos.ShortCollateralCurrency), out var fund)) continue;
            var resBefore = fund.ReservedBalance;
            fund.ReservedBalance += pos.ShortCollateral;
            _ledger.LogFund(pos.UserId, pos.ShortCollateralCurrency, null,
                "Hydrate:ShortCollateral", pos.ShortCollateral,
                resBefore, fund.ReservedBalance, fund.TotalBalance, fund.TotalBalance);
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

            // §P4 Q2: ClampBuys runs LAST, so fund.ReservedBalance already holds any short collateral
            // (filled + resting) seeded by the Backfill passes. Cap buys against the REMAINING headroom
            // (Total − collateral), not gross Total, or buys + collateral could push ReservedBalance >
            // TotalBalance — an invalid Fund (Available < 0; violates CK_Funds_Balance_Invariants on the
            // next persist). Add (not overwrite) so the collateral survives.
            decimal collateralHeld = fund.ReservedBalance;
            decimal budget = fund.TotalBalance - collateralHeld;
            decimal reserved = 0m;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                // §P5b: a short bracket's buy legs (SL pool + buy-limit TPs) are reserved by
                // ReseedBracketCashPools, not the generic clamp — skip them (mirror of ClampSells).
                if (o.IsBracketChild) continue;
                var orderReservation = ReservationMath.RemainingBuyReservation(o);
                if (orderReservation <= 0m) continue;

                if (reserved + orderReservation <= budget)
                {
                    reserved += orderReservation;
                    // Seed per-order field so Σ CurrentBuyReservation (+ collateral) == fund.ReservedBalance
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
                        "would over-reserve funds (budget={Bud}=Total−collateral, alreadyReserved={Res}, orderRemaining={Rem}).",
                        o.OrderId, o.UserId, o.CurrencyType, budget, reserved, orderReservation);
                }
            }
            var resBefore = fund.ReservedBalance;
            fund.ReservedBalance += reserved;
            _ledger.LogFund(fund.UserId, fund.CurrencyType, null, "Hydrate:SeedFund",
                reserved, resBefore, fund.ReservedBalance,
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
                // §3.6 P4: an armed buy-stop reserves cash/budget at arm time but isn't IsOpen;
                // count it (and the short-bracket SL) so its pooled reservation isn't read as phantom.
                if (o.IsOpen || (o.IsArmed && o.IsStopOrder))
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
                // §3.6 P4: an armed sell-stop reserves shares on the Position at arm time but isn't
                // IsOpen; count it (and a long-bracket SL's pooled reservation) so it isn't clamped away.
                if ((o.IsOpen && o.IsLimitOrder) || (o.IsArmed && o.IsStopOrder))
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

            // §F14: a resting short holds place-time cash collateral in Fund.ReservedBalance that is
            // backed by neither an open buy nor a filled-short Position.ShortCollateral. Fold it as an
            // INDEPENDENT check (a PURE short carries CurrentSellReservedQty == 0 and never enters the
            // sell branch above) so a legitimate resting short isn't read as a phantom fund leak.
            if (o.IsSellOrder && o.IsOpen && o.CurrentShortCollateral > 0m)
            {
                var fkey = (o.UserId, o.CurrencyType);
                expectedBalByFund.TryGetValue(fkey, out var csum);
                expectedBalByFund[fkey] = csum + o.CurrentShortCollateral;
                fundOrderCount.TryGetValue(fkey, out var fc);
                fundOrderCount[fkey] = fc + 1;
            }
        }

        // §3.6 P1 (risk #8): short collateral sits in Fund.ReservedBalance but isn't backed
        // by an open buy order — fold it into the expected fund reservation so a legitimate
        // short doesn't read as a phantom leak (or get clamped away below).
        foreach (var kv in _positions)
        {
            ct.ThrowIfCancellationRequested();
            var pos = kv.Value;
            if (pos.ShortCollateral <= 0m) continue;
            var key = (pos.UserId, pos.ShortCollateralCurrency);
            expectedBalByFund.TryGetValue(key, out var sum);
            expectedBalByFund[key] = sum + pos.ShortCollateral;
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

        // §3.6 P4: include armed buy-stops (their cash/budget pool isn't IsOpen) so the clamp
        // re-derives the SAME expectation the reconcile pass used and never erases the SL pool.
        foreach (var o in _registry.GetArmedBuyStopsForUser(userId, ccy))
            if (o.CurrentBuyReservation > 0m) expected += o.CurrentBuyReservation;

        // §3.6 P1 (risk #8): include short collateral held in this currency so the clamp
        // re-derives the SAME expectation the reconcile pass used and never erases collateral.
        foreach (var kv in _positions)
        {
            var pos = kv.Value;
            if (pos.UserId == userId && pos.ShortCollateral > 0m && pos.ShortCollateralCurrency == ccy)
                expected += pos.ShortCollateral;
        }

        // §F14: include open resting-short collateral holds in this currency so the clamp re-derives
        // the SAME expectation as the reconcile pass and never erases a resting short's place-time hold.
        foreach (var o in _registry.GetOpenShortSellsForUser(userId, ccy))
            if (o.CurrentShortCollateral > 0m) expected += o.CurrentShortCollateral;

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

        // §3.6 P4: include armed sell-stops (their share pool isn't IsOpen) so the clamp matches the
        // reconcile pass and never zeros a standalone armed stop or a long-bracket SL's pooled hold.
        foreach (var o in _registry.GetArmedSellStopsForUser(userId, stockId))
            if (o.CurrentSellReservedQty > 0) expected += o.CurrentSellReservedQty;

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
