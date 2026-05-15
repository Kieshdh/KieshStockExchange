using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using static KieshStockExchange.Services.MarketEngineServices.ReservationMath;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary> Validate, apply, reconcile budget, probe conservation, persist. </summary>
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

        // Gate every (user, resource) touched. Sorted acquisition avoids AB/BA deadlock.
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

            // Register new positions only after commit
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

    /// <summary> Apply deltas inside the caller's ambient tx; caller restores on failure. </summary>
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
        var orderResSnapshots = scope.OrderReservationSnapshots;

        // Snapshot per-order CurrentReservation for every order this batch will touch,
        // so a rollback restores the field along with Fund/Position. We snapshot once per
        // OrderId on first sight — subsequent fills on the same order keep the original
        // pre-batch value.
        void SnapshotOrderIfNew(Order? o)
        {
            if (o is null) return;
            if (!orderResSnapshots.ContainsKey(o.OrderId))
                orderResSnapshots[o.OrderId] = (o.CurrentBuyReservation, o.CurrentSellReservedQty);
        }

        var userSet = new HashSet<int>();
        for (int i = 0; i < trades.Count; i++)
        {
            userSet.Add(trades[i].BuyerId);
            userSet.Add(trades[i].SellerId);
        }
        var userIds = new List<int>(userSet.Count);
        foreach (var id in userSet) userIds.Add(id);

        await _accounts.EnsureLoadedAsync(userIds, ct).ConfigureAwait(false);

        // Validate-pass: filter unhonorable fills into rejected
        var (validateErr, accepted, rejected) = _validator.Filter(
            trades, ordersById, _accounts, pendingNewPositions, ct);
        if (validateErr is not null)
            return (validateErr, Array.Empty<RejectedFill>());

        if (accepted.Count == 0)
            return (null, rejected); // nothing to apply, caller still cancels offending makers

        // Apply-pass: mutate funds + positions on accepted fills only
        var fundMap = new Dictionary<(int, CurrencyType), Fund>();
        var posMap = new Dictionary<(int, int), Position>();
        var newPositionsThisCall = new List<Position>();

        for (int ti = 0; ti < accepted.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var t = accepted[ti];

            var ccy = t.CurrencyType;
            var notional = Round(t.TotalAmount, ccy);

            // Buyer pays from reservation. Limit/Slippage may have over-reserved → unreserve savings.
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
            ordersById.TryGetValue(t.BuyOrderId, out var buyOrder);
            if (buyOrder is not null)
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
                // Drift: should be caught at place time; fail the batch safely if it slips through
                return (OrderResultFactory.OperationFailed(
                    $"Reservation drift on buyer {t.BuyerId}: {ex.Message}"),
                    Array.Empty<RejectedFill>());
            }
            // Lock-step: the buy order's CurrentBuyReservation tracks fund.ReservedBalance.
            // Clamp to handle a TrueMarketBuy whose per-fill notional may briefly exceed the
            // current reservation (the leftover release below covers that) — but in normal
            // limit/slippage paths the consume should equal the notional exactly.
            if (buyOrder is not null)
            {
                SnapshotOrderIfNew(buyOrder);
                var consume = Math.Min(notional, buyOrder.CurrentBuyReservation);
                if (consume > 0m) buyOrder.ConsumeBuyReservation(consume);
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
                if (buyOrder is not null)
                {
                    var consumeSavings = Math.Min(savings, buyOrder.CurrentBuyReservation);
                    if (consumeSavings > 0m) buyOrder.ConsumeBuyReservation(consumeSavings);
                }
                _ledger.LogFund(t.BuyerId, t.CurrencyType, t.BuyOrderId,
                    "ApplyPass:Savings:Unreserve", savings, resB2, buyerFund.ReservedBalance,
                    totB2, buyerFund.TotalBalance);
            }
            buyerFund.UpdatedAt = TimeHelper.NowUtc();

            // Per-fill savings log so modify-buy-above-market releases stay observable
            if (SettlementDebug.Mode && savings > 0m
                && (!SettlementDebug.UserId.HasValue || t.BuyerId == SettlementDebug.UserId.Value))
            {
                _logger.LogInformation(
                    "Savings: buyer #{BuyerId} order #{OrderId} reservedPerUnit={Reserved} fillPrice={FillPrice} qty={Qty} → consumed={Notional} savingsUnreserved={Savings}",
                    t.BuyerId, t.BuyOrderId,
                    ordersById.TryGetValue(t.BuyOrderId, out var bo) ? ReservationPerUnit(bo) : 0m,
                    t.Price, t.Quantity, notional, savings);
            }

            // Seller credit: straight to TotalBalance
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

            // Buyer position: may reuse a position created earlier in the same root tx
            var buyerPosKey = (t.BuyerId, t.StockId);
            if (!posMap.TryGetValue(buyerPosKey, out var buyerPos))
            {
                buyerPos = _accounts.GetPosition(t.BuyerId, t.StockId);
                if (buyerPos is null && !pendingNewPositions.TryGetValue(buyerPosKey, out buyerPos))
                {
                    buyerPos = new Position { UserId = t.BuyerId, StockId = t.StockId };
                    pendingNewPositions[buyerPosKey] = buyerPos;
                    newPositionsThisCall.Add(buyerPos);
                    // brand-new: no snapshot, caller drops on rollback
                }
                else if (buyerPos!.PositionId != 0 && !posSnapshots.ContainsKey(buyerPosKey))
                {
                    posSnapshots[buyerPosKey] = (buyerPos.Quantity, buyerPos.ReservedQuantity);
                }
                posMap[buyerPosKey] = buyerPos;
            }
            buyerPos.Quantity += t.Quantity;
            buyerPos.UpdatedAt = TimeHelper.NowUtc();

            // Seller position: validate-pass already guaranteed sufficient Quantity
            var sellerPosKey = (t.SellerId, t.StockId);
            if (!posMap.TryGetValue(sellerPosKey, out var sellerPos))
            {
                sellerPos = _accounts.GetPosition(t.SellerId, t.StockId);
                if (sellerPos is null) pendingNewPositions.TryGetValue(sellerPosKey, out sellerPos);
                if (sellerPos is null)
                    return (OrderResultFactory.OperationFailed(
                        $"Position not found for seller {t.SellerId} on stock {t.StockId}."),
                        Array.Empty<RejectedFill>());
                posMap[sellerPosKey] = sellerPos;
                if (sellerPos.PositionId != 0 && !posSnapshots.ContainsKey(sellerPosKey))
                    posSnapshots[sellerPosKey] = (sellerPos.Quantity, sellerPos.ReservedQuantity);
            }

            ordersById.TryGetValue(t.SellOrderId, out var sellOrder);

            // Top up reservation from AvailableQuantity for taker sells that skipped place-time reserve
            if (sellerPos.ReservedQuantity < t.Quantity)
            {
                var needed = t.Quantity - sellerPos.ReservedQuantity;
                if (sellerPos.AvailableQuantity < needed)
                    return (OrderResultFactory.OperationFailed(
                        $"Insufficient reservation for seller {t.SellerId} on stock {t.StockId}: " +
                        $"has avail {sellerPos.AvailableQuantity}, needs {needed}."),
                        Array.Empty<RejectedFill>());
                sellerPos.ReserveStock(needed);
                if (sellOrder is not null)
                {
                    SnapshotOrderIfNew(sellOrder);
                    sellOrder.TakeSellReservation(needed);
                }
            }
            sellerPos.ConsumeReservedStock(t.Quantity);
            if (sellOrder is not null)
            {
                SnapshotOrderIfNew(sellOrder);
                var consumeQty = Math.Min(t.Quantity, sellOrder.CurrentSellReservedQty);
                if (consumeQty > 0) sellOrder.ConsumeSellReservation(consumeQty);
            }
        }

        // Materialise mutated entities for the DB write step
        var loadedFunds = new List<Fund>(fundMap.Count);
        foreach (var kv in fundMap) loadedFunds.Add(kv.Value);
        var loadedPositions = new List<Position>();
        foreach (var kv in posMap)
            if (kv.Value.PositionId != 0) loadedPositions.Add(kv.Value);

        // TrueMarketBuy: decrement BuyBudget by batch spend
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

                // Filled TrueMarketBuy with leftover budget: release to avoid phantom Reserved
                if (o.Status == Order.Statuses.Filled && o.BuyBudget > 0m)
                {
                    var leftover = o.BuyBudget.Value;
                    if (fundMap.TryGetValue((o.UserId, o.CurrencyType), out var leftoverFund))
                    {
                        var toRelease = Math.Min(leftover, leftoverFund.ReservedBalance); // clamp against concurrent release
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
                        // Drop per-order reservation in lock-step with the cache aggregate.
                        SnapshotOrderIfNew(o);
                        o.ReleaseBuyReservation();
                        o.BuyBudget = 0m;
                    }
                }
            }
        }

        // Conservation invariant: fund + share deltas must sum to 0 per ccy / stock
        _probe.Check(fundMap, fundSnapshots, posMap, posSnapshots, accepted);

        // DB writes on the caller's ambient root tx — accepted only
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

    /// <summary> Restore cache from scope snapshots. Idempotent. </summary>
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

        // Restore per-order reservation fields in lock-step with Fund/Position restoration.
        foreach (var (orderId, prev) in scope.OrderReservationSnapshots)
        {
            if (!ordersById.TryGetValue(orderId, out var o)) continue;
            o.RestoreReservationFromSnapshot(prev.Buy, prev.Sell);
        }
    }
}
