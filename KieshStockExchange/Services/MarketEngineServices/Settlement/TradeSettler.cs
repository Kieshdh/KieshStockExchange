using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Trade settlement: validate-pass filters fills the seller can't honor, apply-pass
/// mutates funds/positions per fill, TrueMarketBuy budget is reconciled, the conservation
/// probe runs, and the accepted trades + mutated entities are persisted on the caller's
/// ambient root tx. The single-call wrapper owns its own tx.
/// </summary>
internal sealed class TradeSettler
{
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IReservationLedger _ledger;
    private readonly ILogger<TradeSettler> _logger;
    private readonly SellerCapacityValidator _validator;
    private readonly ConservationProbe _probe;

    public TradeSettler(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, ILogger<TradeSettler> logger,
        SellerCapacityValidator validator, ConservationProbe probe)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleAsync(
        IReadOnlyList<Transaction> trades, Dictionary<int, Order> ordersById,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (trades is null || trades.Count == 0)
            return (null, Array.Empty<RejectedFill>());

        // Eagerly enumerate every (user, resource) touched by these trades so we can
        // serialise the apply-pass against any concurrent flow on the same user. Mirror
        // the apply-pass exactly: buyer/seller funds in the trade currency, plus buyer/
        // seller positions on the trade stock. Sorted acquisition inside
        // AcquireUserGatesAsync prevents AB/BA deadlocks across overlapping batches.
        var fundKeys = new HashSet<(int, CurrencyType)>();
        var posKeys = new HashSet<(int, int)>();
        var userSet = new HashSet<int>();
        for (int i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
            fundKeys.Add((t.BuyerId,  t.CurrencyType));
            fundKeys.Add((t.SellerId, t.CurrencyType));
            posKeys.Add((t.BuyerId,  t.StockId));
            posKeys.Add((t.SellerId, t.StockId));
            userSet.Add(t.BuyerId);
            userSet.Add(t.SellerId);
        }
        var userIds = new List<int>(userSet);
        await _accounts.EnsureLoadedAsync(userIds, ct).ConfigureAwait(false);
        await using var gates = await _accounts.AcquireUserGatesAsync(fundKeys, posKeys, ct).ConfigureAwait(false);

        // Single-call wrapper: own the tx, own the snapshots, restore on failure.
        var scope = new TradeBatchScope();

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (err, rejected) = await SettleNoTxAsync(trades, ordersById, scope, ct)
                .ConfigureAwait(false);
            if (err != null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                RestoreSnapshots(ordersById, scope);
                return (err, Array.Empty<RejectedFill>());
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Only register newly-created positions in the cache after the tx commits.
            foreach (var pos in scope.PendingNewPositions.Values)
                _accounts.TrackNewPosition(pos);

            return (null, rejected);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            RestoreSnapshots(ordersById, scope);
            _logger.LogError(ex, "SettleTradesAsync failed");
            return (OrderResultFactory.OperationFailed($"SettleTrades failed: {ex.Message}"),
                    Array.Empty<RejectedFill>());
        }
    }

    /// <summary>
    /// Apply trade deltas to the in-memory cache + DB inside the caller's ambient transaction.
    /// All mutations are tracked into the caller-supplied snapshot dictionaries; the caller
    /// is responsible for rolling them back via <see cref="RestoreSnapshots"/> if the
    /// outer transaction fails.
    /// </summary>
    public async Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        TradeBatchScope scope,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (trades is null || trades.Count == 0)
            return (null, Array.Empty<RejectedFill>());
        if (scope is null) throw new ArgumentNullException(nameof(scope));

        var fundSnapshots = scope.FundSnapshots;
        var posSnapshots = scope.PosSnapshots;
        var budgetSnapshots = scope.BudgetSnapshots;
        var pendingNewPositions = scope.PendingNewPositions;

        var userSet = new HashSet<int>();
        for (int i = 0; i < trades.Count; i++)
        {
            userSet.Add(trades[i].BuyerId);
            userSet.Add(trades[i].SellerId);
        }
        var userIds = new List<int>(userSet.Count);
        foreach (var id in userSet) userIds.Add(id);

        await _accounts.EnsureLoadedAsync(userIds, ct).ConfigureAwait(false);

        // ---------- Validate-pass: filter fills the seller can't honor. No mutations. ----------
        // Mirror the apply-pass budget logic so a fill that the apply-pass would reject
        // (insufficient AvailableQuantity + existing reservation) gets caught here as a
        // recoverable RejectedFill instead of escalating into a fatal "Insufficient
        // reservation" OperationFailed that aborts the whole batch.
        var (validateErr, accepted, rejected) = _validator.Filter(
            trades, ordersById, _accounts, pendingNewPositions, ct);
        if (validateErr is not null)
            return (validateErr, Array.Empty<RejectedFill>());

        // No accepted fills — nothing to apply, no DB writes. Return rejected list so the
        // caller can still cancel the offending makers.
        if (accepted.Count == 0)
            return (null, rejected);

        // ---------- Apply-pass: per-trade fund + position mutations on accepted only. ----------
        var fundMap = new Dictionary<(int, CurrencyType), Fund>();
        var posMap = new Dictionary<(int, int), Position>();
        var newPositionsThisCall = new List<Position>();

        for (int ti = 0; ti < accepted.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var t = accepted[ti];

            var ccy = t.CurrencyType;
            var notional = Round(t.TotalAmount, ccy);

            // Buyer pays from the reservation taken at place time (or in Phase 1.5 of the
            // batch path). Limit/Slippage buys reserved at the upper-bound price; the actual
            // notional may be lower, so the difference is unreserved as "slippage savings".
            // TrueMarketBuy reservation == per-fill notional, so no savings.
            var buyerKey = (t.BuyerId, ccy);
            if (!fundMap.TryGetValue(buyerKey, out var buyerFund))
            {
                buyerFund = _accounts.GetFund(t.BuyerId, ccy);
                if (buyerFund is null)
                    return (OrderResultFactory.OperationFailed($"Fund not found for buyer {t.BuyerId}."),
                            Array.Empty<RejectedFill>());
                fundMap[buyerKey] = buyerFund;
                if (!fundSnapshots.ContainsKey(buyerKey))
                    fundSnapshots[buyerKey] = (buyerFund.TotalBalance, buyerFund.ReservedBalance);
            }

            decimal savings = 0m;
            if (ordersById.TryGetValue(t.BuyOrderId, out var buyOrder))
            {
                var perUnitReserved = ReservationPerUnit(buyOrder);
                if (perUnitReserved > 0m)
                {
                    var rawSavings = (perUnitReserved - t.Price) * t.Quantity;
                    if (rawSavings > 0m) savings = Round(rawSavings, ccy);
                }
            }

            var apResBefore = buyerFund.ReservedBalance;
            var apTotBefore = buyerFund.TotalBalance;
            try
            {
                buyerFund.ConsumeReservedFunds(notional);
            }
            catch (ArgumentException ex)
            {
                // Reservation drift — should be caught at place time, but fail the batch
                // safely if we ever land here so the caller can roll the tx back.
                return (OrderResultFactory.OperationFailed(
                    $"Reservation drift on buyer {t.BuyerId}: {ex.Message}"),
                    Array.Empty<RejectedFill>());
            }
            _ledger.LogFund(t.BuyerId, t.CurrencyType, t.BuyOrderId,
                "ApplyPass:ConsumeReserved", notional, apResBefore, buyerFund.ReservedBalance,
                apTotBefore, buyerFund.TotalBalance);
            if (savings > 0m)
            {
                var resB2 = buyerFund.ReservedBalance;
                var totB2 = buyerFund.TotalBalance;
                try { buyerFund.UnreserveFunds(savings); }
                catch (ArgumentException ex)
                {
                    return (OrderResultFactory.OperationFailed(
                        $"Savings-unreserve drift on buyer {t.BuyerId}: {ex.Message}"),
                        Array.Empty<RejectedFill>());
                }
                _ledger.LogFund(t.BuyerId, t.CurrencyType, t.BuyOrderId,
                    "ApplyPass:Savings:Unreserve", savings, resB2, buyerFund.ReservedBalance,
                    totB2, buyerFund.TotalBalance);
            }
            buyerFund.UpdatedAt = TimeHelper.NowUtc();

            // Diagnostic: log savings unreserve so we can verify modify-buy-above-market
            // releases (perUnitReserved − fillPrice) × qty back to Available. Without
            // this, an apparent "funds lost" report is hard to pin down — the math is
            // correct in code, but a single log line per fill makes it observable.
            if (SettlementDebug.Mode && savings > 0m
                && (!SettlementDebug.UserId.HasValue || t.BuyerId == SettlementDebug.UserId.Value))
            {
                _logger.LogInformation(
                    "Savings: buyer #{BuyerId} order #{OrderId} reservedPerUnit={Reserved} fillPrice={FillPrice} qty={Qty} → consumed={Notional} savingsUnreserved={Savings}",
                    t.BuyerId, t.BuyOrderId,
                    ordersById.TryGetValue(t.BuyOrderId, out var bo) ? ReservationPerUnit(bo) : 0m,
                    t.Price, t.Quantity, notional, savings);
            }

            // Seller receives — straight credit to TotalBalance, no reservation involved.
            var sellerKey = (t.SellerId, ccy);
            if (!fundMap.TryGetValue(sellerKey, out var sellerFund))
            {
                sellerFund = _accounts.GetFund(t.SellerId, ccy);
                if (sellerFund is null)
                    return (OrderResultFactory.OperationFailed($"Fund not found for seller {t.SellerId}."),
                            Array.Empty<RejectedFill>());
                fundMap[sellerKey] = sellerFund;
                if (!fundSnapshots.ContainsKey(sellerKey))
                    fundSnapshots[sellerKey] = (sellerFund.TotalBalance, sellerFund.ReservedBalance);
            }
            sellerFund.TotalBalance += notional;
            sellerFund.UpdatedAt = TimeHelper.NowUtc();

            // Buyer position — may reuse a position created earlier in the same root tx.
            var buyerPosKey = (t.BuyerId, t.StockId);
            if (!posMap.TryGetValue(buyerPosKey, out var buyerPos))
            {
                buyerPos = _accounts.GetPosition(t.BuyerId, t.StockId);
                if (buyerPos is null && !pendingNewPositions.TryGetValue(buyerPosKey, out buyerPos))
                {
                    buyerPos = new Position { UserId = t.BuyerId, StockId = t.StockId };
                    pendingNewPositions[buyerPosKey] = buyerPos;
                    newPositionsThisCall.Add(buyerPos);
                    // No snapshot for brand-new positions — caller drops the entry on rollback.
                }
                else if (buyerPos!.PositionId != 0 && !posSnapshots.ContainsKey(buyerPosKey))
                {
                    posSnapshots[buyerPosKey] = (buyerPos.Quantity, buyerPos.ReservedQuantity);
                }
                posMap[buyerPosKey] = buyerPos;
            }
            buyerPos.Quantity += t.Quantity;
            buyerPos.UpdatedAt = TimeHelper.NowUtc();

            // Seller position — pre-validated above to have sufficient Quantity; lookup chain
            // is identical to the validate-pass.
            var sellerPosKey = (t.SellerId, t.StockId);
            if (!posMap.TryGetValue(sellerPosKey, out var sellerPos))
            {
                sellerPos = _accounts.GetPosition(t.SellerId, t.StockId);
                if (sellerPos is null) pendingNewPositions.TryGetValue(sellerPosKey, out sellerPos);
                // Validate-pass already guaranteed non-null; this assertion is defensive.
                if (sellerPos is null)
                    return (OrderResultFactory.OperationFailed(
                        $"Position not found for seller {t.SellerId} on stock {t.StockId}."),
                        Array.Empty<RejectedFill>());
                posMap[sellerPosKey] = sellerPos;
                if (sellerPos.PositionId != 0 && !posSnapshots.ContainsKey(sellerPosKey))
                    posSnapshots[sellerPosKey] = (sellerPos.Quantity, sellerPos.ReservedQuantity);
            }

            // Sell orders are reserved at place time; settlement consumes the reservation.
            // If the reservation is short (e.g., a taker that immediately matches without
            // having been pre-reserved by the caller), top it up from AvailableQuantity.
            if (sellerPos.ReservedQuantity < t.Quantity)
            {
                var needed = t.Quantity - sellerPos.ReservedQuantity;
                if (sellerPos.AvailableQuantity < needed)
                    return (OrderResultFactory.OperationFailed(
                        $"Insufficient reservation for seller {t.SellerId} on stock {t.StockId}: " +
                        $"has avail {sellerPos.AvailableQuantity}, needs {needed}."),
                        Array.Empty<RejectedFill>());
                sellerPos.ReserveStock(needed);
            }
            sellerPos.ConsumeReservedStock(t.Quantity);
        }

        // Materialize the existing cached entities mutated this call for the DB write step.
        var loadedFunds = new List<Fund>(fundMap.Count);
        foreach (var kv in fundMap) loadedFunds.Add(kv.Value);
        var loadedPositions = new List<Position>();
        foreach (var kv in posMap)
            if (kv.Value.PositionId != 0) loadedPositions.Add(kv.Value);

        // TrueMarketBuy: decrement BuyBudget by total spend for this batch (accepted fills only).
        Dictionary<int, decimal>? spendByOrderId = null;
        for (int i = 0; i < accepted.Count; i++)
        {
            var t = accepted[i];
            if (!ordersById.TryGetValue(t.BuyOrderId, out var o)) continue;
            if (!IsTrueMarketBuy(o) || !o.BuyBudget.HasValue) continue;

            var contribution = Round(t.TotalAmount, t.CurrencyType);
            if (contribution <= 0m) continue;

            spendByOrderId ??= new Dictionary<int, decimal>();
            spendByOrderId.TryGetValue(t.BuyOrderId, out var sum);
            spendByOrderId[t.BuyOrderId] = sum + contribution;
        }

        if (spendByOrderId is not null)
        {
            foreach (var (orderId, spent) in spendByOrderId)
            {
                if (!ordersById.TryGetValue(orderId, out var o)) continue;
                if (!budgetSnapshots.ContainsKey(orderId))
                    budgetSnapshots[orderId] = o.BuyBudget;
                o.BuyBudget = Math.Max(0m, Round(o.BuyBudget!.Value - spent, o.CurrencyType));

                // Reservation-leak fix: a TrueMarketBuy that fully fills at prices below
                // its budgeted average leaves BuyBudget > 0 with no remaining quantity to
                // spend it on. The order's Status is Filled (matcher set it during the
                // apply loop) so CancelRemainderAsync never fires, and the apply-pass
                // doesn't release "savings" for TrueMarketBuy (ReservationPerUnit returns
                // 0 by design). Without this release the leftover sits on Fund.Reserved
                // permanently — the reconciler observed users with $100k+ phantom reserved
                // balance and zero open buys, all from cumulative full-fill leftovers.
                //
                // Limit / Slippage buys aren't affected because their apply-pass already
                // releases (perUnit − fillPrice) × fillQty as savings per fill.
                if (o.Status == Order.Statuses.Filled && o.BuyBudget > 0m)
                {
                    var leftover = o.BuyBudget.Value;
                    if (fundMap.TryGetValue((o.UserId, o.CurrencyType), out var leftoverFund))
                    {
                        // Clamp to live ReservedBalance — defensive against a concurrent
                        // release on the same fund (per-user gate makes this rare, but
                        // we don't want a stale snapshot to break the apply-pass).
                        var toRelease = Math.Min(leftover, leftoverFund.ReservedBalance);
                        if (toRelease > 0m)
                        {
                            var resB = leftoverFund.ReservedBalance;
                            var totB = leftoverFund.TotalBalance;
                            leftoverFund.UnreserveFunds(toRelease);
                            leftoverFund.UpdatedAt = TimeHelper.NowUtc();
                            _ledger.LogFund(o.UserId, o.CurrencyType, orderId,
                                "ApplyPass:TrueMarketBuy:Leftover:Unreserve", toRelease,
                                resB, leftoverFund.ReservedBalance, totB, leftoverFund.TotalBalance);
                        }
                        o.BuyBudget = 0m;
                    }
                }
            }
        }

        // Money-conservation probe: sum (post − pre) TotalBalance across every Fund this
        // call mutated, grouped by currency. Apply-pass debits the buyer and credits the
        // seller by the same `notional` per fill, so the net MUST be 0 within rounding.
        // A non-zero net means a mutation was applied to one side but not the other — a
        // hard bug that would account for the "chart rising upwards" cash leak. Same idea
        // for Position.Quantity per stock: buyer +qty, seller −qty, sum = 0. Both probes
        // log loudly so the next run pinpoints which call produced the asymmetry.
        _probe.Check(fundMap, fundSnapshots, posMap, posSnapshots, accepted);

        // DB writes happen on the caller's ambient root tx — no BeginTransactionAsync here.
        // Persist accepted trades only; rejected ones never happened.
        await _db.InsertAllAsync(accepted, ct).ConfigureAwait(false);
        await _db.UpdateAllAsync(ordersById.Values, ct).ConfigureAwait(false);
        if (loadedFunds.Count > 0)
            await _db.UpdateAllAsync(loadedFunds, ct).ConfigureAwait(false);
        if (loadedPositions.Count > 0)
            await _db.UpdateAllAsync(loadedPositions, ct).ConfigureAwait(false);
        if (newPositionsThisCall.Count > 0)
            await _db.InsertAllAsync(newPositionsThisCall, ct).ConfigureAwait(false);

        return (null, rejected);
    }

    /// <summary>
    /// Restore the cache instances mutated by <see cref="SettleNoTxAsync"/> back to
    /// the values captured in the snapshot dictionaries. Idempotent — safe to call after a
    /// successful settle (snapshots are simply re-applied with the same values, no-op).
    /// </summary>
    public void RestoreSnapshots(Dictionary<int, Order> ordersById, TradeBatchScope scope)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));

        foreach (var (orderId, budget) in scope.BudgetSnapshots)
            if (ordersById.TryGetValue(orderId, out var o))
                o.BuyBudget = budget;

        foreach (var (key, prev) in scope.FundSnapshots)
        {
            var f = _accounts.GetFund(key.UserId, key.Ccy);
            if (f != null)
            {
                f.TotalBalance = prev.Total;
                f.ReservedBalance = prev.Reserved;
            }
        }

        foreach (var (key, prev) in scope.PosSnapshots)
        {
            var p = _accounts.GetPosition(key.UserId, key.StockId);
            if (p != null)
            {
                p.Quantity = prev.Quantity;
                p.ReservedQuantity = prev.Reserved;
            }
        }
    }
}
