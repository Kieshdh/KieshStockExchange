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
                // Round 2 §0009 (Path 2): cold-hydrate over-reserves vs the live FlipQuantity rule.
                // The parent Order isn't loaded here (this path operates on the children list only),
                // so we conservatively pool against `held` — the BracketCoordinator's first event
                // after recovery (OnChildFillShortAsync or OnStopFiringShortAsync) will resize the
                // pool to min(held, parent.FlipQuantity) and release the cushion. This stays
                // bracket-local + invariant-safe; the only cost is a temporary over-reservation
                // until the next bracket event resizes.
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
}
