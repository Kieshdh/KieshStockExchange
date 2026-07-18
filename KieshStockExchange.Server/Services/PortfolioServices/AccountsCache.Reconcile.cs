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
}
