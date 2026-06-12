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
    private readonly IOrderRegistry _registry;

    public TradeSettler(IDataBaseService db, IAccountsCache accounts,
        IReservationLedger ledger, ILogger<TradeSettler> logger,
        SellerCapacityValidator validator, ConservationProbe probe, IOrderRegistry registry)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    // §P6: fund a short-bracket TP's buyback from its sibling SL's shared cash pool. The TP (a bracket-child
    // buy) reserves 0 of its own; its sibling SL is an armed buy-stop holding the pool as CurrentBuyReservation.
    // Draw up to `want` from that pool, reducing the fund's ReservedBalance AND the SL's pool field in lock-step
    // so the coordinator's later poolDrop resize releases only the remaining cushion (no double-release/drift).
    // Returns the amount actually drawn (0 when the buy isn't a bracket child or has no sibling pool).
    private decimal DrawSiblingSlPool(Order? buyOrder, int buyerId, CurrencyType ccy, Fund buyerFund, decimal want)
    {
        if (want <= 0m || buyOrder is not { IsBracketChild: true }) return 0m;
        Order? sl = null;
        foreach (var o in _registry.GetArmedBuyStopsForUser(buyerId, ccy))
            if (o.ParentOrderId == buyOrder.ParentOrderId && o.CurrentBuyReservation > 0m) { sl = o; break; }
        if (sl is null) return 0m;
        var fromPool = Math.Min(want, Math.Min(sl.CurrentBuyReservation, buyerFund.ReservedBalance));
        if (fromPool <= 0m) return 0m;
        var resB = buyerFund.ReservedBalance; var totB = buyerFund.TotalBalance;
        buyerFund.ConsumeReservedFunds(fromPool);   // pay the buyback from the pool (Reserved + Total down)
        buyerFund.UpdatedAt = TimeHelper.NowUtc();
        sl.ConsumeBuyReservation(fromPool);          // keep the SL pool field in lock-step with the fund
        _ledger.LogFund(buyerId, ccy, buyOrder.OrderId, "ApplyPass:TPBuyback:DrawSLPool",
            fromPool, resB, buyerFund.ReservedBalance, totB, buyerFund.TotalBalance);
        return fromPool;
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
        var posCollSnapshots = scope.PosShortCollateralSnapshots;
        var budgetSnapshots = scope.BudgetSnapshots;
        var pendingNewPositions = scope.PendingNewPositions;
        var orderResSnapshots = scope.OrderReservationSnapshots;

        // Snapshot per-order CurrentReservation for every order this batch will touch,
        // so a rollback restores the field along with Fund/Position. We snapshot once per
        // OrderId on first sight — subsequent fills on the same order keep the original
        // pre-batch value.
        //
        // R4 §0001: the matcher (MatchingEngine.Match + OrderBook.ApplyMakerFill) captures
        // pre-match Status into scope.OrderStatusSnapshots before Order.Fill mutates it,
        // so the dict is already authoritative by the time the settler sees these orders.
        // The TryAdd below is defence-in-depth: it catches the brand-new-Pending-fails-
        // at-settler edge where the matcher didn't see the order (e.g., a reservation-only
        // failure with no fills emitted). First-sight semantics: matcher-side capture wins
        // because it runs before any settler write.
        void SnapshotOrderIfNew(Order? o)
        {
            if (o is null) return;
            if (!orderResSnapshots.ContainsKey(o.OrderId))
                orderResSnapshots[o.OrderId] = (o.CurrentBuyReservation, o.CurrentSellReservedQty, o.CurrentShortCollateral);
            scope.OrderStatusSnapshots.TryAdd(o.OrderId, o.Status);
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
            var notional = CurrencyHelper.RoundMoney(t.TotalAmount, ccy);

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
                    if (rawSavings > 0m) savings = CurrencyHelper.RoundMoney(rawSavings, ccy);
                }
            }

            var apResBefore = buyerFund.ReservedBalance;
            var apTotBefore = buyerFund.TotalBalance;
            // §P6: consume only this buy order's OWN reservation from ReservedBalance — never more. A buyer
            // who also holds shorts carries that collateral in the same ReservedBalance, so a blind
            // ConsumeReservedFunds(notional) on an over-budget fill would eat into it. Consume up to the
            // order's CurrentBuyReservation, then pay any excess from AVAILABLE cash (never reserved).
            var reservedPortion = buyOrder is not null
                ? Math.Min(notional, buyOrder.CurrentBuyReservation)
                : notional;
            var excess = notional - reservedPortion;
            try
            {
                if (reservedPortion > 0m) buyerFund.ConsumeReservedFunds(reservedPortion);
                if (excess > 0m)
                {
                    // §P6: a short-bracket TP reserves 0 of its own — its buyback is funded by the sibling
                    // SL's shared cash pool (Model B). Draw the excess from that pool FIRST: consume it from
                    // the fund AND shrink the SL's CurrentBuyReservation in lock-step, so the SL's pool field
                    // stays accurate and the coordinator's poolDrop resize releases only the remaining cushion
                    // (no double-release, no drift). Falling straight to AvailableBalance instead would fail a
                    // heavily-committed bot whose buyback cash is locked in the reserved pool, not free.
                    var fromPool = DrawSiblingSlPool(buyOrder, t.BuyerId, ccy, buyerFund, excess);
                    excess -= fromPool;
                    if (excess > 0m) buyerFund.WithdrawFunds(excess);   // remainder from available
                }
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
                var orderBefore = buyOrder.CurrentBuyReservation;
                var consume = Math.Min(notional, buyOrder.CurrentBuyReservation);
                if (consume > 0m) buyOrder.ConsumeBuyReservation(consume);
                _ledger.LogOrder(buyOrder.UserId, buyOrder.OrderId, "ApplyPass:ConsumeReserved",
                    consume, orderBefore, buyOrder.CurrentBuyReservation,
                    buyOrder.CurrentSellReservedQty, buyOrder.CurrentSellReservedQty);
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
                    var orderBefore = buyOrder.CurrentBuyReservation;
                    var consumeSavings = Math.Min(savings, buyOrder.CurrentBuyReservation);
                    if (consumeSavings > 0m) buyOrder.ConsumeBuyReservation(consumeSavings);
                    _ledger.LogOrder(buyOrder.UserId, buyOrder.OrderId, "ApplyPass:Savings:Unreserve",
                        consumeSavings, orderBefore, buyOrder.CurrentBuyReservation,
                        buyOrder.CurrentSellReservedQty, buyOrder.CurrentSellReservedQty);
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
            var sellerTotBefore = sellerFund.TotalBalance;
            sellerFund.TotalBalance += notional;
            sellerFund.UpdatedAt = TimeHelper.NowUtc();
            _ledger.LogFund(t.SellerId, t.CurrencyType, t.SellOrderId,
                "ApplyPass:SellerCredit", notional, sellerFund.ReservedBalance, sellerFund.ReservedBalance,
                sellerTotBefore, sellerFund.TotalBalance);

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
                    posCollSnapshots[buyerPosKey] = (buyerPos.ShortCollateral, buyerPos.ShortCollateralCurrency);
                }
                posMap[buyerPosKey] = buyerPos;
            }
            var buyerPosQtyBefore = buyerPos.Quantity;
            buyerPos.Quantity += t.Quantity;
            buyerPos.UpdatedAt = TimeHelper.NowUtc();
            _ledger.LogPosition(t.BuyerId, t.StockId, t.BuyOrderId, "ApplyPass:BuyerCredit",
                t.Quantity, buyerPos.ReservedQuantity, buyerPos.ReservedQuantity,
                buyerPosQtyBefore, buyerPos.Quantity);

            // §3.6 P1 buy-to-close: if the buyer was short before this fill, the buy reduces
            // the short toward zero — release the proportional cash collateral back to
            // AvailableBalance. Collateral is a ReservedBalance lock (never TotalBalance),
            // so this is invisible to the conservation probe; realized P/L already sits in
            // TotalBalance via the consume above and the original short proceeds.
            if (buyerPosQtyBefore < 0 && buyerPos.ShortCollateral > 0m)
            {
                if (buyerPos.ShortCollateralCurrency == ccy)
                {
                    var coverQty = Math.Min(t.Quantity, -buyerPosQtyBefore);
                    // Full cover (now flat or long) MUST clear ALL remaining position collateral — the DB
                    // invariant forbids a non-negative position carrying collateral. A partial cover (still
                    // short) releases pro-rata; residual collateral on a still-short position is legal.
                    var posRelease = buyerPos.Quantity >= 0
                        ? buyerPos.ShortCollateral
                        : CurrencyHelper.RoundMoney(
                            buyerPos.ShortCollateral * coverQty / -buyerPosQtyBefore, ccy);
                    posRelease = Math.Min(posRelease, buyerPos.ShortCollateral);
                    // §P6: decouple the POSITION release from the FUND unreserve. The position release is
                    // authoritative for the invariant and is applied in full; the fund unreserve is clamped to
                    // what's actually reserved. If the fund falls a few units short (SL-pool vs collateral
                    // rounding when a short-bracket SL fires and covers — its buyback consume and this release
                    // both draw ReservedBalance), the position is still cleared and the tiny fund
                    // over-reservation is left for the reconciler to clamp — never hard-fail the settle and
                    // desync order vs position.
                    var fundRelease = Math.Min(posRelease, buyerFund.ReservedBalance);
                    if (posRelease > 0m)
                    {
                        var cResB = buyerFund.ReservedBalance;
                        var cTotB = buyerFund.TotalBalance;
                        if (fundRelease > 0m) buyerFund.UnreserveFunds(fundRelease);
                        buyerFund.UpdatedAt = TimeHelper.NowUtc();
                        buyerPos.ReleaseShortCollateral(posRelease);
                        _ledger.LogFund(t.BuyerId, ccy, t.BuyOrderId,
                            "ApplyPass:ShortClose:ReleaseCollateral", -fundRelease,
                            cResB, buyerFund.ReservedBalance, cTotB, buyerFund.TotalBalance);
                        _ledger.LogPosition(t.BuyerId, t.StockId, t.BuyOrderId,
                            "ApplyPass:ShortClose:ReleaseCollateral", 0,
                            buyerPos.ReservedQuantity, buyerPos.ReservedQuantity,
                            buyerPos.Quantity, buyerPos.Quantity);
                        if (fundRelease < posRelease)
                            _logger.LogWarning(
                                "Short-close collateral shortfall buyer {Buyer} stock {Stock}: position released " +
                                "{Pos} but fund had only {Fund} reserved ({Diff} left for reconciler) — likely " +
                                "SL-pool/collateral rounding on a short-bracket SL fire.",
                                t.BuyerId, t.StockId, posRelease, fundRelease, posRelease - fundRelease);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Short-close currency mismatch buyer {Buyer} stock {Stock}: collateral in " +
                        "{CollCcy}, close in {Ccy}; collateral not released this fill.",
                        t.BuyerId, t.StockId, buyerPos.ShortCollateralCurrency, ccy);
                }
            }

            // Resolve the sell order up front — short detection needs its type.
            ordersById.TryGetValue(t.SellOrderId, out var sellOrder);

            // Seller position: validate-pass already guaranteed either sufficient long
            // Quantity (long sell) or a collateral-backed short open (flat seller).
            var sellerPosKey = (t.SellerId, t.StockId);
            if (!posMap.TryGetValue(sellerPosKey, out var sellerPos))
            {
                sellerPos = _accounts.GetPosition(t.SellerId, t.StockId);
                if (sellerPos is null) pendingNewPositions.TryGetValue(sellerPosKey, out sellerPos);

                // A flat seller's market sell opens a short; create the row if it never existed.
                var startQty0 = sellerPos?.Quantity ?? 0;
                bool willShort = sellOrder is not null && sellOrder.IsMarketOrder && startQty0 <= 0;
                if (sellerPos is null)
                {
                    if (!willShort)
                        return (OrderResultFactory.OperationFailed(
                            $"Position not found for seller {t.SellerId} on stock {t.StockId}."),
                            Array.Empty<RejectedFill>());
                    sellerPos = new Position { UserId = t.SellerId, StockId = t.StockId };
                    pendingNewPositions[sellerPosKey] = sellerPos;
                    newPositionsThisCall.Add(sellerPos);
                }
                else if (sellerPos.PositionId != 0 && !posSnapshots.ContainsKey(sellerPosKey))
                {
                    posSnapshots[sellerPosKey] = (sellerPos.Quantity, sellerPos.ReservedQuantity);
                    posCollSnapshots[sellerPosKey] = (sellerPos.ShortCollateral, sellerPos.ShortCollateralCurrency);
                }
                posMap[sellerPosKey] = sellerPos;
            }

            // Pre-batch Quantity decides short vs long (stable across this order's fills and
            // matching SellerCapacityValidator's decision). New rows were flat ⇒ 0.
            int sellerStartQty = posSnapshots.TryGetValue(sellerPosKey, out var sps) ? sps.Quantity : 0;
            // §F14: a resting LIMIT short (carrying a place-time collateral hold) opens/extends a short
            // at fill just like a market short — pure short when flat (startQty<=0), straddle/flip when a
            // long holder's covered shares are exhausted (startQty>0). The short blocks below source the
            // collateral from the order's hold (already reserved) instead of ReserveFunds.
            bool sellHasShortPart = sellOrder is not null
                && (sellOrder.IsMarketOrder || (sellOrder.IsLimitOrder && sellOrder.CurrentShortCollateral > 0m));
            bool isShortFill = sellHasShortPart && sellerStartQty <= 0;
            bool isFlipFill = sellHasShortPart && sellerStartQty > 0;

            if (isShortFill)
            {
                // §3.6 P1 short open: reserve cash collateral == this fill's proceeds (just
                // credited above), so AvailableBalance covers it and buying power is unchanged
                // at open. Push Quantity negative; collateral lives on the position. No
                // ReservedQuantity (shares) involved.
                var collateral = ReservationMath.ShortCollateralForFill(t.Quantity, t.Price, ccy);
                // §F14: a resting short's collateral is already in ReservedBalance from placement —
                // consume the order's hold (snapshot for rollback), don't re-reserve. A market short
                // reserves it now (P1). Either way it's posted to Position.ShortCollateral below, so
                // Σ holds == Fund.ReservedBalance is preserved.
                if (sellOrder!.CurrentShortCollateral >= collateral)
                {
                    SnapshotOrderIfNew(sellOrder);
                    sellOrder.ConsumeShortCollateral(collateral);
                }
                else
                {
                    var cResB = sellerFund.ReservedBalance;
                    var cTotB = sellerFund.TotalBalance;
                    sellerFund.ReserveFunds(collateral);
                    sellerFund.UpdatedAt = TimeHelper.NowUtc();
                    _ledger.LogFund(t.SellerId, ccy, t.SellOrderId,
                        "ApplyPass:ShortOpen:ReserveCollateral", collateral,
                        cResB, sellerFund.ReservedBalance, cTotB, sellerFund.TotalBalance);
                }

                var sQtyBefore = sellerPos.Quantity;
                sellerPos.ApplyDelta(-t.Quantity);
                sellerPos.TakeShortCollateral(collateral, ccy);
                _ledger.LogPosition(t.SellerId, t.StockId, t.SellOrderId, "ApplyPass:ShortOpen",
                    -t.Quantity, sellerPos.ReservedQuantity, sellerPos.ReservedQuantity,
                    sQtyBefore, sellerPos.Quantity);

                // R4 §0009 Stage 1: per-fill branch + trade price. Behaviour-neutral when off.
                if (MatchSymmetryProbe.Enabled)
                    MatchSymmetryProbe.Record("settler", "sell", "short_open", t.Price);
            }
            else if (isFlipFill)
            {
                // §3.6 risk #7 long→short flip: split this crossing fill at the order's remaining
                // long reservation. CurrentSellReservedQty is the order's OWN long pool (seeded to
                // the held shares at place time, drawn down per fill) — using it, not live
                // Position.Quantity (which other orders' reservations also move), keeps the long
                // part bounded to THIS order. Consume the long part FIRST so ReservedQuantity
                // reaches 0 before ApplyDelta pushes Quantity negative (a short can't hold a share
                // reservation — Position.ApplyDelta guards this).
                //
                // Round 2 §0008 (Path 2): a bracket parent's FlipQuantity is the round-2 way of
                // saying "the design expects (qty - FlipQuantity) of this to round-trip and
                // FlipQuantity to flip into a new short". longPart+shortPart should equal qty;
                // anything else is a bug at the decision/sizing layer.
                int longPart = Math.Min(t.Quantity, sellOrder!.CurrentSellReservedQty);
                int shortPart = t.Quantity - longPart;
                System.Diagnostics.Debug.Assert(longPart + shortPart == t.Quantity,
                    "TradeSettler.isFlipFill: longPart+shortPart must equal trade qty.");
                if (sellOrder.FlipQuantity > 0 && SettlementDebug.Mode)
                {
                    // When Path 2 emitted this order, FlipQuantity > 0 telegraphs the expected
                    // shortPart magnitude (over the full multi-fill lifetime of the order).
                    // Debug-only sanity log; not gated for correctness — the existing path is
                    // structurally correct via CurrentSellReservedQty alone.
                    _logger.LogDebug(
                        "Flip settle (Path 2): order #{Id} qty {Q} (this fill) longPart={Lp} shortPart={Sp} declared FlipQty={Fq}",
                        sellOrder.OrderId, t.Quantity, longPart, shortPart, sellOrder.FlipQuantity);
                }

                if (longPart > 0)
                {
                    var flipResBefore = sellerPos.ReservedQuantity;
                    var flipQtyBefore = sellerPos.Quantity;
                    sellerPos.ConsumeReservedStock(longPart);
                    _ledger.LogPosition(t.SellerId, t.StockId, t.SellOrderId, "ApplyPass:Flip:ConsumeReservedStock",
                        longPart, flipResBefore, sellerPos.ReservedQuantity,
                        flipQtyBefore, sellerPos.Quantity);
                    SnapshotOrderIfNew(sellOrder);
                    var orderBefore = sellOrder.CurrentSellReservedQty;
                    sellOrder.ConsumeSellReservation(longPart);
                    _ledger.LogOrder(sellOrder.UserId, sellOrder.OrderId, "ApplyPass:Flip:ConsumeSellReservation",
                        longPart, sellOrder.CurrentBuyReservation, sellOrder.CurrentBuyReservation,
                        orderBefore, sellOrder.CurrentSellReservedQty);
                }

                if (shortPart > 0)
                {
                    // Round 2 Q7 fix: defer the collateral commit until AFTER ApplyDelta and check
                    // the LIVE Position.Quantity. The pre-batch sellerStartQty (captured at line 427)
                    // can be stale by the time this flip path runs — intra-batch buys on the same
                    // (user, stock) Position can have lifted the live Quantity above zero, so the
                    // "shortPart" doesn't actually push the position negative. In that case the
                    // collateral logic must be SKIPPED entirely: the trade is effectively a normal
                    // long-close sell. Diagnostic 03936ec captured exactly this scenario (Order
                    // 18708 ShortBracket FlipQty=1, post-ApplyDelta Q=+24, collateral=406.02
                    // tripping CK_Positions_Quantity_Invariants).
                    var sQtyBefore = sellerPos.Quantity;
                    sellerPos.ApplyDelta(-shortPart);

                    if (sellerPos.Quantity >= 0)
                    {
                        // Intra-batch buys lifted live Q before this flip path ran. No short
                        // actually opened — skip the collateral reservation. The sellOrder's
                        // CurrentSellReservedQty was already consumed by the longPart branch
                        // above; the shortPart "consumed" reservation is no-op because the order
                        // had no remaining sell reservation to consume (longPart took it all).
                        _ledger.LogPosition(t.SellerId, t.StockId, t.SellOrderId,
                            "ApplyPass:Flip:NoopShortPart", -shortPart,
                            sellerPos.ReservedQuantity, sellerPos.ReservedQuantity,
                            sQtyBefore, sellerPos.Quantity);
                    }
                    else
                    {
                        // Real short open — reserve collateral against the new negative inventory.
                        var collateral = ReservationMath.ShortCollateralForFill(shortPart, t.Price, ccy);
                        // §F14: resting-short straddle — consume the order's place-time hold (already in
                        // ReservedBalance); a market flip reserves now. SnapshotOrderIfNew already ran for the
                        // long part above, but call again (idempotent — snapshots once per OrderId).
                        if (sellOrder!.CurrentShortCollateral >= collateral)
                        {
                            SnapshotOrderIfNew(sellOrder);
                            sellOrder.ConsumeShortCollateral(collateral);
                        }
                        else
                        {
                            var cResB = sellerFund.ReservedBalance;
                            var cTotB = sellerFund.TotalBalance;
                            sellerFund.ReserveFunds(collateral);
                            sellerFund.UpdatedAt = TimeHelper.NowUtc();
                            _ledger.LogFund(t.SellerId, ccy, t.SellOrderId,
                                "ApplyPass:Flip:ShortOpen:ReserveCollateral", collateral,
                                cResB, sellerFund.ReservedBalance, cTotB, sellerFund.TotalBalance);
                        }

                        sellerPos.TakeShortCollateral(collateral, ccy);
                        _ledger.LogPosition(t.SellerId, t.StockId, t.SellOrderId, "ApplyPass:Flip:ShortOpen",
                            -shortPart, sellerPos.ReservedQuantity, sellerPos.ReservedQuantity,
                            sQtyBefore, sellerPos.Quantity);
                    }
                }

                // R4 §0009 Stage 1: flip branch (mixed long-close + new short). Trade price
                // is the same for both parts so a single row covers the fill.
                if (MatchSymmetryProbe.Enabled)
                    MatchSymmetryProbe.Record("settler", "sell", "flip", t.Price);
            }
            else
            {
                // Long sell — top up reservation from AvailableQuantity for taker sells that
                // skipped place-time reserve, then consume.
                if (sellerPos.ReservedQuantity < t.Quantity)
                {
                    var needed = t.Quantity - sellerPos.ReservedQuantity;
                    if (sellerPos.AvailableQuantity < needed)
                        return (OrderResultFactory.OperationFailed(
                            $"Insufficient reservation for seller {t.SellerId} on stock {t.StockId}: " +
                            $"has avail {sellerPos.AvailableQuantity}, needs {needed}."),
                            Array.Empty<RejectedFill>());
                    var posResBefore = sellerPos.ReservedQuantity;
                    sellerPos.ReserveStock(needed);
                    _ledger.LogPosition(t.SellerId, t.StockId, t.SellOrderId, "ApplyPass:TakerSellTopUp",
                        needed, posResBefore, sellerPos.ReservedQuantity,
                        sellerPos.Quantity, sellerPos.Quantity);
                    if (sellOrder is not null)
                    {
                        SnapshotOrderIfNew(sellOrder);
                        var orderBefore = sellOrder.CurrentSellReservedQty;
                        sellOrder.TakeSellReservation(needed);
                        _ledger.LogOrder(sellOrder.UserId, sellOrder.OrderId, "ApplyPass:TakerSellTopUp",
                            needed, sellOrder.CurrentBuyReservation, sellOrder.CurrentBuyReservation,
                            orderBefore, sellOrder.CurrentSellReservedQty);
                    }
                }
                var sellerPosResBefore = sellerPos.ReservedQuantity;
                sellerPos.ConsumeReservedStock(t.Quantity);
                _ledger.LogPosition(t.SellerId, t.StockId, t.SellOrderId, "ApplyPass:ConsumeReservedStock",
                    t.Quantity, sellerPosResBefore, sellerPos.ReservedQuantity,
                    sellerPos.Quantity, sellerPos.Quantity);
                if (sellOrder is not null)
                {
                    SnapshotOrderIfNew(sellOrder);
                    var orderBefore = sellOrder.CurrentSellReservedQty;
                    var consumeQty = Math.Min(t.Quantity, sellOrder.CurrentSellReservedQty);
                    if (consumeQty > 0) sellOrder.ConsumeSellReservation(consumeQty);
                    _ledger.LogOrder(sellOrder.UserId, sellOrder.OrderId, "ApplyPass:ConsumeSellReservation",
                        consumeQty, sellOrder.CurrentBuyReservation, sellOrder.CurrentBuyReservation,
                        orderBefore, sellOrder.CurrentSellReservedQty);
                }

                // R4 §0009 Stage 1: plain long-close branch — pairs with short_open / flip
                // entries above so the script can compute per-side trade-price residuals.
                if (MatchSymmetryProbe.Enabled)
                    MatchSymmetryProbe.Record("settler", "sell", "long_close", t.Price);
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

            var contribution = CurrencyHelper.RoundMoney(t.TotalAmount, t.CurrencyType);
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
                o.BuyBudget = Math.Max(0m, CurrencyHelper.RoundMoney(o.BuyBudget!.Value - spent, o.CurrencyType));

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
                        var orderBefore = o.CurrentBuyReservation;
                        var released = o.ReleaseBuyReservation();
                        o.BuyBudget = 0m;
                        _ledger.LogOrder(o.UserId, o.OrderId, "ApplyPass:TrueMarketBuy:Leftover:Release",
                            released, orderBefore, o.CurrentBuyReservation,
                            o.CurrentSellReservedQty, o.CurrentSellReservedQty);
                    }
                }
            }
        }

        // Conservation invariant: fund + share deltas must sum to 0 per ccy / stock
        _probe.Check(fundMap, fundSnapshots, posMap, posSnapshots, accepted);

        // DB writes on the caller's ambient root tx — accepted only
        for (int i = 0; i < accepted.Count; i++)
        {
            var t = accepted[i];
            _ledger.LogTransaction(t.BuyerId, t.SellerId, t.StockId, t.CurrencyType,
                t.BuyOrderId, t.SellOrderId, t.Quantity, t.Price, t.TotalAmount);
        }
        await _db.InsertAllAsync(accepted, ct).ConfigureAwait(false);
        await _db.UpdateAllAsync(ordersById.Values, ct).ConfigureAwait(false);
        if (loadedFunds.Count > 0)
            await _db.UpdateAllAsync(loadedFunds, ct).ConfigureAwait(false);
        if (loadedPositions.Count > 0)
        {
            // R3 §0001 (Q7 structural defense). Walk to-be-written Positions and verify each
            // one's triple satisfies the DB `CK_Positions_Quantity_Invariants` constraint. Was
            // a diagnostic in round 2 (log-and-continue, let the DB constraint reject); now
            // detect-and-reject BEFORE the DB write so the failure surface is a clean engine
            // OperationFailed instead of a DbException. The forensic ERROR log still names the
            // offending Position triple + order context.
            //
            // Happy-path byte-identical to round 2: no Position violates → no detection → same
            // flow. Unhappy-path failure mode shifts from CK-rejection-with-cascading-effects
            // to engine-side rejection that travels the well-tested rollback path with full
            // snapshot restoration (Funds + Positions + Order reservations + Order Status).
            var offender = FindInvariantViolation(loadedPositions, ordersById);
            if (offender is not null)
            {
                return (OrderResultFactory.OperationFailed(
                    $"Q7 pre-write CK violation: Position #{offender.PositionId} " +
                    $"user={offender.UserId} stock={offender.StockId} " +
                    $"Q={offender.Quantity} R={offender.ReservedQuantity} " +
                    $"SC={offender.ShortCollateral} SCcy={offender.ShortCollateralCurrencyCode}"),
                    Array.Empty<RejectedFill>());
            }
            await _db.UpdateAllAsync(loadedPositions, ct).ConfigureAwait(false);
        }
        if (newPositionsThisCall.Count > 0)
        {
            var offender = FindInvariantViolation(newPositionsThisCall, ordersById);
            if (offender is not null)
            {
                return (OrderResultFactory.OperationFailed(
                    $"Q7 pre-insert CK violation: Position user={offender.UserId} stock={offender.StockId} " +
                    $"Q={offender.Quantity} R={offender.ReservedQuantity} " +
                    $"SC={offender.ShortCollateral} SCcy={offender.ShortCollateralCurrencyCode}"),
                    Array.Empty<RejectedFill>());
            }
            await _db.InsertAllAsync(newPositionsThisCall, ct).ConfigureAwait(false);
        }

        return (null, rejected);
    }

    // R3 §0001 (Q7 structural defense). Replaces the round-2 diagnostic at the same site.
    // Walks Positions about to be persisted, returns the first one violating the DB
    // CK_Positions_Quantity_Invariants check (mirrored in Position.IsValid at Position.cs:98-100).
    // Caller converts a non-null return into an OperationFailed that travels the rollback path.
    // Logs the same ERROR line the round-2 diagnostic emitted so forensic log greps still hit.
    private Position? FindInvariantViolation(
        IReadOnlyList<Position> positions, Dictionary<int, Order> ordersById)
    {
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            bool valid =
                p.ReservedQuantity >= 0
                && p.ReservedQuantity <= Math.Max(p.Quantity, 0)
                && p.ShortCollateral >= 0m
                && (p.Quantity >= 0 || p.ReservedQuantity == 0)
                && (p.Quantity < 0 || p.ShortCollateral == 0m);
            if (!valid)
            {
                _logger.LogError(
                    "Q7 pre-write CK violation: Position #{Pid} user={U} stock={S} Q={Q} R={R} SC={SC} SCcy={SCcy} " +
                    "(batch had {OrderCount} order(s) touching this batch)",
                    p.PositionId, p.UserId, p.StockId, p.Quantity, p.ReservedQuantity,
                    p.ShortCollateral, p.ShortCollateralCurrencyCode, ordersById.Count);
                return p;
            }
        }
        return null;
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
                var resBefore = f.ReservedBalance;
                var totBefore = f.TotalBalance;
                f.TotalBalance = prev.Total;
                f.ReservedBalance = prev.Reserved;
                _ledger.LogFund(key.UserId, key.Ccy, null,
                    "RestoreSnapshots:Fund", prev.Reserved - resBefore,
                    resBefore, f.ReservedBalance, totBefore, f.TotalBalance);
            }
        }

        foreach (var (key, prev) in scope.PosSnapshots)
        {
            var p = _accounts.GetPosition(key.UserId, key.StockId);
            if (p != null)
            {
                var posResBefore = p.ReservedQuantity;
                var posQtyBefore = p.Quantity;
                p.Quantity = prev.Quantity;
                p.ReservedQuantity = prev.Reserved;
                _ledger.LogPosition(key.UserId, key.StockId, null,
                    "RestoreSnapshots:Position", prev.Reserved - posResBefore,
                    posResBefore, p.ReservedQuantity, posQtyBefore, p.Quantity);
            }
        }

        // Restore short collateral in lock-step with the Position/Fund snapshots above
        // (Fund.ReservedBalance is restored via FundSnapshots; this restores the position's
        // ShortCollateral field that the open/close mutated).
        foreach (var (key, prev) in scope.PosShortCollateralSnapshots)
        {
            var p = _accounts.GetPosition(key.UserId, key.StockId);
            if (p != null)
            {
                p.ShortCollateral = prev.Collateral;
                p.ShortCollateralCurrency = prev.Ccy;
            }
        }

        // Restore per-order reservation fields in lock-step with Fund/Position restoration.
        foreach (var (orderId, prev) in scope.OrderReservationSnapshots)
        {
            if (!ordersById.TryGetValue(orderId, out var o))
            {
                // Hypothesized leak source: snapshot exists but order isn't in this
                // ordersById, so its CBR/CSR isn't restored even though Fund was.
                // Log the user from the snapshot key's currency partner... we don't
                // have the user here, but the orderId is enough to reconstruct after.
                _ledger.LogOrder(0, orderId, "RestoreSnapshots:OrderSkipped",
                    prev.Buy, prev.Buy, prev.Buy, prev.Sell, prev.Sell);
                continue;
            }
            var orderBuyBefore = o.CurrentBuyReservation;
            var orderSellBefore = o.CurrentSellReservedQty;
            o.RestoreReservationFromSnapshot(prev.Buy, prev.Sell, prev.ShortCollateral);
            _ledger.LogOrder(o.UserId, o.OrderId, "RestoreSnapshots:Order",
                prev.Buy - orderBuyBefore, orderBuyBefore, o.CurrentBuyReservation,
                orderSellBefore, o.CurrentSellReservedQty);
        }

        // R3 §0001: restore pre-batch Order.Status. The matcher mutates Status to
        // Filled / PartiallyFilled before SettleNoTxAsync's DB writes; without this restore a
        // pre-write rejection (or any post-match settle failure) would leave the in-memory
        // order at Filled while the DB row is back to its pre-batch status. The same
        // order↔position desync mode warned about at TradeSettler:354-360 in the §P6
        // precedent. The snapshot dict is populated by the matcher's existing reservation-
        // snapshot pathway — see TradeBatchScope.OrderStatusSnapshots XML doc for the hook.
        foreach (var (orderId, prevStatus) in scope.OrderStatusSnapshots)
        {
            if (!ordersById.TryGetValue(orderId, out var o)) continue;
            if (o.Status != prevStatus) o.Status = prevStatus;
        }
    }
}
