using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// A fill the validate-pass rejected because the seller can't honor it. The caller
/// (OrderExecutionService) cancels the offending maker order, rolls back the matcher's
/// effect on the maker via book.RollbackMakerFill, and reduces the taker's AmountFilled.
/// </summary>
public sealed record RejectedFill(Transaction Trade, int MakerOrderId, string Reason);

public interface ISettlementEngine
{
    /// <summary> Balance check and order persist — no reservation writes </summary>
    Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default);

    /// <summary>
    /// Persist all trades and transfer assets in a single batched DB transaction.
    /// Returns the optional fatal error (DB write fails, missing fund/position rows) and a
    /// list of fills the seller couldn't honor. Rejected fills never touch the cache or DB —
    /// the caller is expected to cancel the offending maker order and roll back the
    /// matcher's per-fill effect on the book.
    /// </summary>
    Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesAsync(
        IReadOnlyList<Transaction> trades, Dictionary<int, Order> ordersById,
        CancellationToken ct = default);

    /// <summary>
    /// Settle a batch of trades inside a caller-owned transaction. Mutates the cached
    /// Fund/Position instances and BuyBudget on TrueMarketBuy orders, recording the previous
    /// values into the supplied snapshot dictionaries so the caller can roll the cache back
    /// if its outer transaction fails. <paramref name="pendingNewPositions"/> is shared
    /// across multiple invocations within one root tx so a (user, stock) created in one
    /// settle call is reused by the next instead of duplicated.
    ///
    /// Returns the optional fatal error and a list of rejected fills (seller short of stock).
    /// Rejected fills never touch the cache or DB.
    /// </summary>
    Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)> fundSnapshots,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        Dictionary<int, decimal?> budgetSnapshots,
        Dictionary<(int, int), Position> pendingNewPositions,
        CancellationToken ct = default);

    /// <summary>
    /// Roll back the in-memory cache mutations recorded by <see cref="SettleTradesNoTxAsync"/>.
    /// Use this from the catch block of an outer transaction whose commit failed.
    /// </summary>
    void RestoreCacheSnapshots(
        Dictionary<int, Order> ordersById,
        Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)> fundSnapshots,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        Dictionary<int, decimal?> budgetSnapshots);

    /// <summary> Mark an order as cancelled </summary>
    Task CancelRemainderAsync(Order order, CancellationToken ct = default);

    /// <summary> Update price/quantity on an existing open order </summary>
    Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default);
}

public sealed class SettlementEngine : ISettlementEngine
{
    // Diagnostic switches mirroring MatchingEngine. Flip DebugMode on to surface
    // savings-unreserve events per fill so we can verify modify-then-fill
    // releases the correct slippage savings back to the buyer's Available.
    // DebugUserId filters to a single user (admin) to keep bot churn silent.
    private readonly bool DebugMode = true;
    private readonly int? DebugUserId = 20001;

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly ILogger<SettlementEngine> _logger;

    public SettlementEngine(IDataBaseService db, IAccountsCache accounts, ILogger<SettlementEngine> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Order settlement
    public async Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Cache covers fund + position for this user across all currencies/stocks. Cold-load
        // happens at most once per user; warm calls are O(1).
        await _accounts.EnsureLoadedAsync(incoming.UserId, ct).ConfigureAwait(false);

        // Per-user reservation gate: serialise this user's Fund.ReservedBalance / Position.
        // ReservedQuantity mutation against any other concurrent flow that touches the same
        // (user, resource). Pre-1b this was masked by _writeGate; post-1b the batch path
        // releases _writeGate between groups, so the race becomes real and the gate is the
        // only protection. Released on scope exit via IAsyncDisposable.
        await using var gate = incoming.IsBuyOrder
            ? await _accounts.AcquireFundGateAsync(incoming.UserId, incoming.CurrencyType, ct).ConfigureAwait(false)
            : await _accounts.AcquirePositionGateAsync(incoming.UserId, incoming.StockId, ct).ConfigureAwait(false);

        // Read-only balance check from cache — no DB hit on warm path. Reserve at place
        // time so subsequent same-account orders see the reduced AvailableBalance /
        // AvailableQuantity (multi-order over-promise rejected here, not at settlement).
        Position? sellPos = null;
        Fund? buyFund = null;
        decimal buyReservation = 0m;
        if (incoming.IsBuyOrder)
        {
            buyFund = _accounts.GetFund(incoming.UserId, incoming.CurrencyType);
            buyReservation = InitialBuyReservation(incoming);

            if (buyFund == null)
            {
                return OrderResultFactory.InsufficientFunds(
                    $"Order requires {CurrencyHelper.Format(buyReservation, incoming.CurrencyType)}: " +
                    $"no fund row for user {incoming.UserId} in {incoming.CurrencyType}.");
            }
            if (buyFund.AvailableBalance < buyReservation)
            {
                return OrderResultFactory.InsufficientFunds(
                    $"Order requires {CurrencyHelper.Format(buyReservation, incoming.CurrencyType)} " +
                    $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, incoming.CurrencyType)} is available " +
                    $"(Total={CurrencyHelper.Format(buyFund.TotalBalance, incoming.CurrencyType)}, " +
                    $"Reserved={CurrencyHelper.Format(buyFund.ReservedBalance, incoming.CurrencyType)}).");
            }

            try { buyFund.ReserveFunds(buyReservation); }
            catch (ArgumentException)
            {
                // Race against another reserver — emit the same enriched diagnostic.
                return OrderResultFactory.InsufficientFunds(
                    $"Order requires {CurrencyHelper.Format(buyReservation, incoming.CurrencyType)} " +
                    $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, incoming.CurrencyType)} is available " +
                    $"(Total={CurrencyHelper.Format(buyFund.TotalBalance, incoming.CurrencyType)}, " +
                    $"Reserved={CurrencyHelper.Format(buyFund.ReservedBalance, incoming.CurrencyType)}); " +
                    $"race on ReserveFunds.");
            }
        }
        else
        {
            sellPos = _accounts.GetPosition(incoming.UserId, incoming.StockId);
            // AvailableQuantity = Quantity - ReservedQuantity. Reserved already accounts for
            // this user's other open sells in the book, so a multi-order over-promise is
            // rejected at place time instead of at settlement.
            if (sellPos == null)
            {
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s): " +
                    $"no position row for user {incoming.UserId} on stock {incoming.StockId}.");
            }
            if (sellPos.AvailableQuantity < incoming.Quantity)
            {
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s) but only {sellPos.AvailableQuantity} available " +
                    $"(Quantity={sellPos.Quantity}, Reserved={sellPos.ReservedQuantity}).");
            }

            try { sellPos.ReserveStock(incoming.Quantity); }
            catch (ArgumentException)
            {
                // Lost a race against another reserver — same enriched diagnostic.
                return OrderResultFactory.InsufficientStocks(
                    $"Order requires {incoming.Quantity} share(s) but only {sellPos.AvailableQuantity} available " +
                    $"(Quantity={sellPos.Quantity}, Reserved={sellPos.ReservedQuantity}); race on ReserveStock.");
            }
        }

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await _db.CreateOrder(incoming, ct).ConfigureAwait(false);

            // Persist the reservation we just took on the cached fund/position so
            // IUserPortfolioService.RefreshAsync (which reads from DB) sees the new
            // ReservedBalance / ReservedQuantity. Without this, the AccountPage Funds
            // card and TopNavBar funds chip stay stuck on the pre-place balance until
            // the order eventually fills (or never, if it rests on the book).
            if (buyFund is not null && buyReservation > 0m)
                await _db.UpdateAllAsync(new[] { buyFund }, ct).ConfigureAwait(false);
            if (sellPos is not null)
                await _db.UpdateAllAsync(new[] { sellPos }, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            // Persist failed — release the reservation we just took so the cache stays
            // consistent. Clamp rather than try/catch: a concurrent flow could have
            // touched ReservedQuantity / ReservedBalance between our reserve and
            // this rollback, and the first-chance exception under 20k bots floods
            // the debugger even when handled.
            if (sellPos is not null)
            {
                var toRelease = Math.Min(incoming.Quantity, sellPos.ReservedQuantity);
                if (toRelease > 0) sellPos.UnreserveStock(toRelease);
            }
            if (buyFund is not null && buyReservation > 0m)
            {
                var toRelease = Math.Min(buyReservation, buyFund.ReservedBalance);
                if (toRelease > 0m) buyFund.UnreserveFunds(toRelease);
            }
            _logger.LogError(ex, "SettleOrderAsync failed to persist order");
            return OrderResultFactory.OperationFailed($"Failed to persist order: {ex.Message}");
        }
    }
    #endregion

    #region Trade settlement
    public async Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesAsync(
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
        var fundSnapshots = new Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)>();
        var posSnapshots = new Dictionary<(int, int), (int Quantity, int Reserved)>();
        var budgetSnapshots = new Dictionary<int, decimal?>();
        var pendingNewPositions = new Dictionary<(int, int), Position>();

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (err, rejected) = await SettleTradesNoTxAsync(trades, ordersById,
                fundSnapshots, posSnapshots, budgetSnapshots, pendingNewPositions, ct)
                .ConfigureAwait(false);
            if (err != null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                RestoreCacheSnapshots(ordersById, fundSnapshots, posSnapshots, budgetSnapshots);
                return (err, Array.Empty<RejectedFill>());
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Only register newly-created positions in the cache after the tx commits.
            foreach (var pos in pendingNewPositions.Values)
                _accounts.TrackNewPosition(pos);

            return (null, rejected);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            RestoreCacheSnapshots(ordersById, fundSnapshots, posSnapshots, budgetSnapshots);
            _logger.LogError(ex, "SettleTradesAsync failed");
            return (OrderResultFactory.OperationFailed($"SettleTrades failed: {ex.Message}"),
                    Array.Empty<RejectedFill>());
        }
    }

    /// <summary>
    /// Apply trade deltas to the in-memory cache + DB inside the caller's ambient transaction.
    /// All mutations are tracked into the caller-supplied snapshot dictionaries; the caller
    /// is responsible for rolling them back via <see cref="RestoreCacheSnapshots"/> if the
    /// outer transaction fails.
    /// </summary>
    public async Task<(OrderResult? Error, IReadOnlyList<RejectedFill> Rejected)> SettleTradesNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)> fundSnapshots,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        Dictionary<int, decimal?> budgetSnapshots,
        Dictionary<(int, int), Position> pendingNewPositions,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (trades is null || trades.Count == 0)
            return (null, Array.Empty<RejectedFill>());

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
        //
        // Two pools per seller:
        //   • availableBySeller[(sellerId, stockId)] = AvailableQuantity (unreserved stock,
        //     drawn from when a fill needs more than the maker's existing reservation).
        //   • reservedRemainingByOrder[sellOrderId] = the seller order's RemainingQuantity
        //     at start of batch (its pre-fill reservation pool). Each fill consumes from
        //     its own order's reservation first, then tops up from available.
        var availableBySeller = new Dictionary<(int, int), int>(trades.Count);
        var reservedRemainingByOrder = new Dictionary<int, int>(trades.Count);
        var rejected = new List<RejectedFill>();
        var accepted = new List<Transaction>(trades.Count);

        for (int ti = 0; ti < trades.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var t = trades[ti];
            var sellerKey = (t.SellerId, t.StockId);

            // Lazy-init available pool from the seller's current AvailableQuantity.
            if (!availableBySeller.TryGetValue(sellerKey, out var available))
            {
                var sellerPos = _accounts.GetPosition(t.SellerId, t.StockId);
                if (sellerPos is null && !pendingNewPositions.TryGetValue(sellerKey, out sellerPos))
                    return (OrderResultFactory.OperationFailed(
                        $"Position not found for seller {t.SellerId} on stock {t.StockId}."),
                        Array.Empty<RejectedFill>());
                available = sellerPos!.AvailableQuantity;
                availableBySeller[sellerKey] = available;
            }

            // Lazy-init the maker order's reservation pool from its RemainingQuantity.
            // ordersById is keyed by OrderId; t.SellOrderId is always the seller's order id
            // (regardless of which side was the taker). Orders not in ordersById have no
            // pre-existing reservation (e.g. brand-new market sells created mid-batch).
            if (!reservedRemainingByOrder.TryGetValue(t.SellOrderId, out var reservedThis))
            {
                reservedThis = ordersById.TryGetValue(t.SellOrderId, out var sellOrder)
                    ? sellOrder.RemainingQuantity
                    : 0;
                reservedRemainingByOrder[t.SellOrderId] = reservedThis;
            }

            // Consume from the order's own reservation first; top-up from available for
            // any deficit. Reject if both pools combined can't cover the fill — the
            // offending maker will be cancelled by the caller (RollbackRejectedFills).
            var fromReserved = Math.Min(reservedThis, t.Quantity);
            var fromAvailable = t.Quantity - fromReserved;
            if (fromAvailable > available)
            {
                rejected.Add(new RejectedFill(
                    t,
                    t.SellOrderId,
                    $"Insufficient position for seller {t.SellerId} on stock {t.StockId}: " +
                    $"order reservation {reservedThis} + available {available} < needs {t.Quantity}."));
                continue;
            }

            reservedRemainingByOrder[t.SellOrderId] = reservedThis - fromReserved;
            availableBySeller[sellerKey] = available - fromAvailable;
            accepted.Add(t);
        }

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

            try
            {
                buyerFund.ConsumeReservedFunds(notional);
                if (savings > 0m) buyerFund.UnreserveFunds(savings);
            }
            catch (ArgumentException ex)
            {
                // Reservation drift — should be caught at place time, but fail the batch
                // safely if we ever land here so the caller can roll the tx back.
                return (OrderResultFactory.OperationFailed(
                    $"Reservation drift on buyer {t.BuyerId}: {ex.Message}"),
                    Array.Empty<RejectedFill>());
            }
            buyerFund.UpdatedAt = TimeHelper.NowUtc();

            // Diagnostic: log savings unreserve so we can verify modify-buy-above-market
            // releases (perUnitReserved − fillPrice) × qty back to Available. Without
            // this, an apparent "funds lost" report is hard to pin down — the math is
            // correct in code, but a single log line per fill makes it observable.
            if (DebugMode && savings > 0m
                && (!DebugUserId.HasValue || t.BuyerId == DebugUserId.Value))
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
                            leftoverFund.UnreserveFunds(toRelease);
                            leftoverFund.UpdatedAt = TimeHelper.NowUtc();
                        }
                        o.BuyBudget = 0m;
                    }
                }
            }
        }

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
    /// Restore the cache instances mutated by <see cref="SettleTradesNoTxAsync"/> back to
    /// the values captured in the snapshot dictionaries. Idempotent — safe to call after a
    /// successful settle (snapshots are simply re-applied with the same values, no-op).
    /// </summary>
    public void RestoreCacheSnapshots(
        Dictionary<int, Order> ordersById,
        Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)> fundSnapshots,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        Dictionary<int, decimal?> budgetSnapshots)
    {
        foreach (var (orderId, budget) in budgetSnapshots)
            if (ordersById.TryGetValue(orderId, out var o))
                o.BuyBudget = budget;

        foreach (var (key, prev) in fundSnapshots)
        {
            var f = _accounts.GetFund(key.Item1, key.Item2);
            if (f != null)
            {
                f.TotalBalance = prev.Total;
                f.ReservedBalance = prev.Reserved;
            }
        }

        foreach (var (key, prev) in posSnapshots)
        {
            var p = _accounts.GetPosition(key.Item1, key.Item2);
            if (p != null)
            {
                p.Quantity = prev.Quantity;
                p.ReservedQuantity = prev.Reserved;
            }
        }
    }
    #endregion

    #region Cancellation and modification
    public async Task CancelRemainderAsync(Order order, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // EnsureLoaded outside the gate so we never nest _loadGate inside a gate scope.
        await _accounts.EnsureLoadedAsync(order.UserId, ct).ConfigureAwait(false);

        // Per-user gate: hold across release + persist so a concurrent SettleOrderAsync
        // on the same (user, resource) can't observe a half-released reservation.
        await using var gate = order.IsSellOrder
            ? await _accounts.AcquirePositionGateAsync(order.UserId, order.StockId, ct).ConfigureAwait(false)
            : await _accounts.AcquireFundGateAsync(order.UserId, order.CurrencyType, ct).ConfigureAwait(false);

        var dbOrder = await _db.GetOrderById(order.OrderId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Order #{order.OrderId} not found.");

        if (!dbOrder.IsOpen)
        {
            if (order.IsOpen) order.Cancel(); // keep in-memory consistent if needed
            return;
        }

        dbOrder.Cancel();
        await _db.UpdateOrder(dbOrder, ct).ConfigureAwait(false);

        if (order.IsOpen) order.Cancel(); // keep in-memory order consistent

        // Release the unfilled reservation AND persist the resulting Position/Fund to
        // DB so the AvailableQuantity/AvailableBalance visible to the UI (which reads
        // from IUserPortfolioService → DB) actually drops after cancel. Pre-fix this
        // only mutated the cache; DB and IUserPortfolioService stayed stale until the
        // next full refresh.
        await ReleaseSellReservationAndPersist(order, order.RemainingQuantity, ct).ConfigureAwait(false);
        await ReleaseBuyReservationAndPersist(order, ct).ConfigureAwait(false);
    }

    private async Task ReleaseSellReservationAndPersist(Order order, int qty, CancellationToken ct)
    {
        if (qty <= 0 || !order.IsSellOrder) return;
        var pos = _accounts.GetPosition(order.UserId, order.StockId);
        if (pos is null) return;
        // Clamp to live ReservedQuantity instead of try/catch ArgumentException — a peer
        // path (RollbackRejectedFills 5a, CancelOrdersBatchAsync) may already have
        // released some or all of this order's reservation, and under 20k bots the
        // first-chance exception window floods the debugger even when handled.
        var toRelease = Math.Min(qty, pos.ReservedQuantity);
        if (toRelease > 0)
        {
            pos.UnreserveStock(toRelease);
            pos.UpdatedAt = TimeHelper.NowUtc();
            await _db.UpdateAllAsync(new[] { pos }, ct).ConfigureAwait(false);
        }

        if (DebugMode && (!DebugUserId.HasValue || order.UserId == DebugUserId.Value))
            _logger.LogInformation(
                "Cancel: released {Qty} share(s) for order #{OrderId} (user {UserId}, stock {StockId}); available now {Avail}",
                toRelease, order.OrderId, order.UserId, order.StockId, pos.AvailableQuantity);
    }

    private async Task ReleaseBuyReservationAndPersist(Order order, CancellationToken ct)
    {
        if (!order.IsBuyOrder) return;
        var amount = RemainingBuyReservation(order);
        if (amount <= 0m) return;
        var fund = _accounts.GetFund(order.UserId, order.CurrencyType);
        if (fund is null) return;
        var toRelease = Math.Min(amount, fund.ReservedBalance);
        if (toRelease > 0m)
        {
            fund.UnreserveFunds(toRelease);
            fund.UpdatedAt = TimeHelper.NowUtc();
            await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
        }

        if (DebugMode && (!DebugUserId.HasValue || order.UserId == DebugUserId.Value))
            _logger.LogInformation(
                "Cancel: released {Amount} for order #{OrderId} (user {UserId}, {Ccy}); available now {Avail}",
                toRelease, order.OrderId, order.UserId, order.CurrencyType, fund.AvailableBalance);
    }

    public async Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Cache covers fund + position for this user across all currencies/stocks.
        // Without this call, modifies for users not yet trading in this session
        // would find GetFund/GetPosition null and silently skip the reservation
        // delta — funds in DB and admin tables would never see the new reservation.
        await _accounts.EnsureLoadedAsync(order.UserId, ct).ConfigureAwait(false);

        // Per-user gate: a modify mutates either the user's fund reservation (buy) or
        // position reservation (sell). Acquire the matching gate around the delta
        // computation + tx + cache write so a concurrent settle/cancel/place on the
        // same resource doesn't race on ReservedBalance / ReservedQuantity.
        await using var gate = order.IsBuyOrder
            ? await _accounts.AcquireFundGateAsync(order.UserId, order.CurrencyType, ct).ConfigureAwait(false)
            : await _accounts.AcquirePositionGateAsync(order.UserId, order.StockId, ct).ConfigureAwait(false);

        // Compute reservation deltas up front so we can apply them after the tx commits
        // and validate "would the new order overdraft this user?" before mutating the DB.
        // Sell side: delta in reservation == delta in Quantity (RemainingQuantity moves
        // with Quantity since AmountFilled is unchanged here). Buy side: delta is
        // (newPrice × newRemainingQty) − (oldPrice × oldRemainingQty) for limit/slippage;
        // TrueMarketBuy budget is not modifiable so its delta is always 0.
        int sellReservationDelta = 0;
        Position? sellPos = null;
        if (newQuantity.HasValue && order.IsSellOrder)
        {
            sellReservationDelta = newQuantity.Value - order.Quantity;
            if (sellReservationDelta != 0)
            {
                sellPos = _accounts.GetPosition(order.UserId, order.StockId);
                if (sellPos is null)
                    throw new InvalidOperationException(
                        "Could not load your position for this stock. Try reloading the page.");
                if (sellReservationDelta > 0 && sellPos.AvailableQuantity < sellReservationDelta)
                    throw new InvalidOperationException(
                        $"Order needs {sellReservationDelta} more share(s) but only {sellPos.AvailableQuantity} available.");
            }
        }

        decimal buyReservationDelta = 0m;
        Fund? buyFund = null;
        if (order.IsBuyOrder && (newQuantity.HasValue || newPrice.HasValue))
        {
            var oldBuyRes = RemainingBuyReservation(order);
            var newBuyRes = ProjectedBuyReservation(order, newQuantity, newPrice);
            buyReservationDelta = newBuyRes - oldBuyRes;
            if (buyReservationDelta != 0m)
            {
                buyFund = _accounts.GetFund(order.UserId, order.CurrencyType);
                if (buyFund is null)
                    throw new InvalidOperationException(
                        "Could not load your funds for this currency. Try reloading the page.");
                if (buyReservationDelta > 0m && buyFund.AvailableBalance < buyReservationDelta)
                    throw new InvalidOperationException(
                        $"Order needs {CurrencyHelper.Format(buyReservationDelta, order.CurrencyType)} more " +
                        $"but only {CurrencyHelper.Format(buyFund.AvailableBalance, order.CurrencyType)} is available.");
            }
        }

        // Snapshot cache state so we can roll the in-memory fund/position back if the
        // tx fails after we've already mutated them.
        int? sellPosOldReserved = sellPos?.ReservedQuantity;
        decimal? buyFundOldTotal = buyFund?.TotalBalance;
        decimal? buyFundOldReserved = buyFund?.ReservedBalance;

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var dbOrder = await _db.GetOrderById(order.OrderId, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException($"Order #{order.OrderId} not found.");

            if (!dbOrder.IsOpen)
                throw new InvalidOperationException("Only open orders can be modified.");

            if (newPrice.HasValue) dbOrder.UpdatePrice(newPrice.Value);
            if (newQuantity.HasValue) dbOrder.UpdateQuantity(newQuantity.Value);

            await _db.UpdateOrder(dbOrder, ct).ConfigureAwait(false);

            // Keep in-memory order consistent for book and UI
            if (newPrice.HasValue) order.UpdatePrice(newPrice.Value);
            if (newQuantity.HasValue) order.UpdateQuantity(newQuantity.Value);

            // Apply reservation deltas to cache and persist the fund/position inside the
            // same tx so DB and cache stay in sync. Without the persist, the admin tables
            // and a cold-load AccountsCache would never see the new reservation.
            if (sellPos is not null && sellReservationDelta != 0)
            {
                if (sellReservationDelta > 0) sellPos.ReserveStock(sellReservationDelta);
                else sellPos.UnreserveStock(-sellReservationDelta);
                sellPos.UpdatedAt = TimeHelper.NowUtc();
                await _db.UpdateAllAsync(new[] { sellPos }, ct).ConfigureAwait(false);
            }

            if (buyFund is not null && buyReservationDelta != 0m)
            {
                if (buyReservationDelta > 0m) buyFund.ReserveFunds(buyReservationDelta);
                else buyFund.UnreserveFunds(-buyReservationDelta);
                buyFund.UpdatedAt = TimeHelper.NowUtc();
                await _db.UpdateAllAsync(new[] { buyFund }, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);

            // Restore cache mutations — the tx rollback already reverted the DB.
            if (sellPos is not null && sellPosOldReserved.HasValue
                && sellPos.ReservedQuantity != sellPosOldReserved.Value)
            {
                sellPos.ReservedQuantity = sellPosOldReserved.Value;
            }
            if (buyFund is not null && buyFundOldReserved.HasValue)
            {
                buyFund.TotalBalance = buyFundOldTotal!.Value;
                buyFund.ReservedBalance = buyFundOldReserved.Value;
            }
            throw;
        }
    }
    #endregion

    #region Helpers
    private static bool IsTrueMarketBuy(Order o) => o.OrderType == Order.Types.TrueMarketBuy;

    private static decimal Round(decimal amount, CurrencyType ccy)
        => CurrencyHelper.RoundMoney(amount, ccy);

    /// <summary>
    /// Per-unit reservation for a buy order. Returns 0 for non-buys and TrueMarketBuy
    /// (which reserves a flat <see cref="Order.BuyBudget"/> rather than per-unit).
    /// LimitBuy reserves at <see cref="Order.Price"/>; SlippageMarketBuy reserves at the
    /// upper-bound <see cref="Order.PriceWithSlippage"/>.
    /// </summary>
    internal static decimal ReservationPerUnit(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (o.IsLimitOrder) return o.Price;
        if (o.IsSlippageOrder && o.PriceWithSlippage.HasValue) return o.PriceWithSlippage.Value;
        return 0m; // TrueMarketBuy: per-fill reserves directly from BuyBudget
    }

    /// <summary>
    /// Up-front reservation amount for a freshly-placed buy order. For limit and slippage
    /// orders this is per-unit × Quantity; for TrueMarketBuy it's the full BuyBudget.
    /// </summary>
    internal static decimal InitialBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m;
        return Round(ReservationPerUnit(o) * o.Quantity, o.CurrencyType);
    }

    /// <summary>
    /// Reservation still held against the unfilled portion of a buy order. For limit /
    /// slippage orders that's per-unit × RemainingQuantity. For TrueMarketBuy it's the
    /// remaining <see cref="Order.BuyBudget"/> (which the apply-pass decrements per fill).
    /// </summary>
    internal static decimal RemainingBuyReservation(Order o)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m;
        return Round(ReservationPerUnit(o) * o.RemainingQuantity, o.CurrencyType);
    }

    /// <summary>
    /// What <see cref="RemainingBuyReservation"/> would return if the order's price
    /// and/or quantity were changed to the supplied values. Used by
    /// <see cref="ApplyOrderChangeAsync"/> to size the reservation delta before
    /// mutating the order. Caller passes nulls for fields that are not changing.
    /// </summary>
    internal static decimal ProjectedBuyReservation(Order o, int? newQty, decimal? newPrice)
    {
        if (!o.IsBuyOrder) return 0m;
        if (IsTrueMarketBuy(o)) return o.BuyBudget ?? 0m; // budget is not modifiable

        decimal perUnit;
        if (o.IsLimitOrder)
            perUnit = newPrice ?? o.Price;
        else if (o.IsSlippageOrder && o.PriceWithSlippage.HasValue)
            perUnit = o.PriceWithSlippage.Value; // slippage upper bound isn't modified
        else
            return 0m;

        var qty = newQty ?? o.Quantity;
        var remainingQty = Math.Max(0, qty - o.AmountFilled);
        if (remainingQty == 0) return 0m;
        return Round(perUnit * remainingQty, o.CurrencyType);
    }
    #endregion
}
