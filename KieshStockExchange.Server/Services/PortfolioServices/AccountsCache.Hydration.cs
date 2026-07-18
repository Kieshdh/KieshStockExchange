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

public sealed partial class AccountsCache
{
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
}
