using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.PortfolioServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public interface ISettlementEngine
{
    /// <summary> Balance check and order persist — no reservation writes </summary>
    Task<OrderResult?> SettleOrderAsync(Order incoming, CancellationToken ct = default);

    /// <summary> Persist all trades and transfer assets in a single batched DB transaction </summary>
    Task<OrderResult?> SettleTradesAsync(IReadOnlyList<Transaction> trades, Dictionary<int, Order> ordersById, CancellationToken ct = default);

    /// <summary>
    /// Settle a batch of trades inside a caller-owned transaction. Mutates the cached
    /// Fund/Position instances and BuyBudget on TrueMarketBuy orders, recording the previous
    /// values into the supplied snapshot dictionaries so the caller can roll the cache back
    /// if its outer transaction fails. <paramref name="pendingNewPositions"/> is shared
    /// across multiple invocations within one root tx so a (user, stock) created in one
    /// settle call is reused by the next instead of duplicated.
    /// </summary>
    Task<OrderResult?> SettleTradesNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        Dictionary<(int, CurrencyType), decimal> fundSnapshots,
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
        Dictionary<(int, CurrencyType), decimal> fundSnapshots,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        Dictionary<int, decimal?> budgetSnapshots);

    /// <summary> Mark an order as cancelled </summary>
    Task CancelRemainderAsync(Order order, CancellationToken ct = default);

    /// <summary> Update price/quantity on an existing open order </summary>
    Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default);
}

public sealed class SettlementEngine : ISettlementEngine
{
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

        // Read-only balance check from cache — no DB hit on warm path.
        Position? sellPos = null;
        if (incoming.IsBuyOrder)
        {
            var fund = _accounts.GetFund(incoming.UserId, incoming.CurrencyType);
            var required = IsTrueMarketBuy(incoming)
                ? incoming.BuyBudget!.Value
                : Round(incoming.TotalAmount, incoming.CurrencyType);

            if (fund == null || fund.TotalBalance < required)
                return IsTrueMarketBuy(incoming)
                    ? OrderResultFactory.InsufficientFunds($"Insufficient funds for TrueMarketBuy (user {incoming.UserId}).")
                    : OrderResultFactory.InsufficientFunds($"Insufficient funds (user {incoming.UserId}).");
        }
        else
        {
            sellPos = _accounts.GetPosition(incoming.UserId, incoming.StockId);
            // AvailableQuantity = Quantity - ReservedQuantity. Reserved already accounts for
            // this user's other open sells in the book, so a multi-order over-promise is
            // rejected at place time instead of at settlement.
            if (sellPos == null || sellPos.AvailableQuantity < incoming.Quantity)
                return OrderResultFactory.InsufficientStocks($"Insufficient shares for sell order (user {incoming.UserId}).");

            try { sellPos.ReserveStock(incoming.Quantity); }
            catch (ArgumentException)
            {
                // Lost a race against another reserver — treat as insufficient.
                return OrderResultFactory.InsufficientStocks($"Insufficient shares for sell order (user {incoming.UserId}).");
            }
        }

        try
        {
            await _db.CreateOrder(incoming, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            // Persist failed — release the reservation we just took so the cache stays consistent.
            if (sellPos is not null)
            {
                try { sellPos.UnreserveStock(incoming.Quantity); }
                catch (ArgumentException) { /* should not happen; swallow defensively */ }
            }
            _logger.LogError(ex, "SettleOrderAsync failed to persist order");
            return OrderResultFactory.OperationFailed($"Failed to persist order: {ex.Message}");
        }
    }
    #endregion

    #region Trade settlement
    public async Task<OrderResult?> SettleTradesAsync(IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (trades is null || trades.Count == 0) return null;

        // Single-call wrapper: own the tx, own the snapshots, restore on failure.
        var fundSnapshots = new Dictionary<(int, CurrencyType), decimal>();
        var posSnapshots = new Dictionary<(int, int), (int Quantity, int Reserved)>();
        var budgetSnapshots = new Dictionary<int, decimal?>();
        var pendingNewPositions = new Dictionary<(int, int), Position>();

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var err = await SettleTradesNoTxAsync(trades, ordersById,
                fundSnapshots, posSnapshots, budgetSnapshots, pendingNewPositions, ct)
                .ConfigureAwait(false);
            if (err != null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                RestoreCacheSnapshots(ordersById, fundSnapshots, posSnapshots, budgetSnapshots);
                return err;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // Only register newly-created positions in the cache after the tx commits.
            foreach (var pos in pendingNewPositions.Values)
                _accounts.TrackNewPosition(pos);

            return null;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            RestoreCacheSnapshots(ordersById, fundSnapshots, posSnapshots, budgetSnapshots);
            _logger.LogError(ex, "SettleTradesAsync failed");
            return OrderResultFactory.OperationFailed($"SettleTrades failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply trade deltas to the in-memory cache + DB inside the caller's ambient transaction.
    /// All mutations are tracked into the caller-supplied snapshot dictionaries; the caller
    /// is responsible for rolling them back via <see cref="RestoreCacheSnapshots"/> if the
    /// outer transaction fails.
    /// </summary>
    public async Task<OrderResult?> SettleTradesNoTxAsync(
        IReadOnlyList<Transaction> trades,
        Dictionary<int, Order> ordersById,
        Dictionary<(int, CurrencyType), decimal> fundSnapshots,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        Dictionary<int, decimal?> budgetSnapshots,
        Dictionary<(int, int), Position> pendingNewPositions,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (trades is null || trades.Count == 0) return null;

        var userSet = new HashSet<int>();
        for (int i = 0; i < trades.Count; i++)
        {
            userSet.Add(trades[i].BuyerId);
            userSet.Add(trades[i].SellerId);
        }
        var userIds = new List<int>(userSet.Count);
        foreach (var id in userSet) userIds.Add(id);

        await _accounts.EnsureLoadedAsync(userIds, ct).ConfigureAwait(false);

        // Per-call working maps — keyed locally so we don't repeatedly re-snapshot the
        // same instance across multiple trades. Snapshots flow into the caller's dicts.
        var fundMap = new Dictionary<(int, CurrencyType), Fund>();
        var posMap = new Dictionary<(int, int), Position>();
        var newPositionsThisCall = new List<Position>();

        for (int ti = 0; ti < trades.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var t = trades[ti];

            var ccy = t.CurrencyType;
            var notional = Round(t.TotalAmount, ccy);

            // Buyer pays
            var buyerKey = (t.BuyerId, ccy);
            if (!fundMap.TryGetValue(buyerKey, out var buyerFund))
            {
                buyerFund = _accounts.GetFund(t.BuyerId, ccy);
                if (buyerFund is null)
                    return OrderResultFactory.OperationFailed($"Fund not found for buyer {t.BuyerId}.");
                fundMap[buyerKey] = buyerFund;
                if (!fundSnapshots.ContainsKey(buyerKey))
                    fundSnapshots[buyerKey] = buyerFund.TotalBalance;
            }
            buyerFund.TotalBalance -= notional;
            buyerFund.UpdatedAt = TimeHelper.NowUtc();

            // Seller receives
            var sellerKey = (t.SellerId, ccy);
            if (!fundMap.TryGetValue(sellerKey, out var sellerFund))
            {
                sellerFund = _accounts.GetFund(t.SellerId, ccy);
                if (sellerFund is null)
                    return OrderResultFactory.OperationFailed($"Fund not found for seller {t.SellerId}.");
                fundMap[sellerKey] = sellerFund;
                if (!fundSnapshots.ContainsKey(sellerKey))
                    fundSnapshots[sellerKey] = sellerFund.TotalBalance;
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

            // Seller position — same lookup chain. A seller can validly hold a pending-new
            // position if they bought-then-sold within this root tx.
            var sellerPosKey = (t.SellerId, t.StockId);
            if (!posMap.TryGetValue(sellerPosKey, out var sellerPos))
            {
                sellerPos = _accounts.GetPosition(t.SellerId, t.StockId);
                if (sellerPos is null) pendingNewPositions.TryGetValue(sellerPosKey, out sellerPos);
                if (sellerPos is null)
                    return OrderResultFactory.OperationFailed(
                        $"Position not found for seller {t.SellerId} on stock {t.StockId}.");
                posMap[sellerPosKey] = sellerPos;
                if (sellerPos.PositionId != 0 && !posSnapshots.ContainsKey(sellerPosKey))
                    posSnapshots[sellerPosKey] = (sellerPos.Quantity, sellerPos.ReservedQuantity);
            }
            if (sellerPos.Quantity < t.Quantity)
                return OrderResultFactory.OperationFailed(
                    $"Insufficient position for seller {t.SellerId} on stock {t.StockId}: " +
                    $"has {sellerPos.Quantity}, needs {t.Quantity}.");
            // Sell orders are reserved at place time; settlement consumes the reservation.
            // If the reservation is short (e.g., a taker that immediately matches without
            // having been pre-reserved by the caller), top it up from AvailableQuantity.
            if (sellerPos.ReservedQuantity < t.Quantity)
            {
                var needed = t.Quantity - sellerPos.ReservedQuantity;
                if (sellerPos.AvailableQuantity < needed)
                    return OrderResultFactory.OperationFailed(
                        $"Insufficient position for seller {t.SellerId} on stock {t.StockId}: " +
                        $"has {sellerPos.Quantity}, needs {t.Quantity}.");
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

        // TrueMarketBuy: decrement BuyBudget by total spend for this batch.
        Dictionary<int, decimal>? spendByOrderId = null;
        for (int i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
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
            }
        }

        // DB writes happen on the caller's ambient root tx — no BeginTransactionAsync here.
        await _db.InsertAllAsync(trades, ct).ConfigureAwait(false);
        await _db.UpdateAllAsync(ordersById.Values, ct).ConfigureAwait(false);
        if (loadedFunds.Count > 0)
            await _db.UpdateAllAsync(loadedFunds, ct).ConfigureAwait(false);
        if (loadedPositions.Count > 0)
            await _db.UpdateAllAsync(loadedPositions, ct).ConfigureAwait(false);
        if (newPositionsThisCall.Count > 0)
            await _db.InsertAllAsync(newPositionsThisCall, ct).ConfigureAwait(false);

        return null;
    }

    /// <summary>
    /// Restore the cache instances mutated by <see cref="SettleTradesNoTxAsync"/> back to
    /// the values captured in the snapshot dictionaries. Idempotent — safe to call after a
    /// successful settle (snapshots are simply re-applied with the same values, no-op).
    /// </summary>
    public void RestoreCacheSnapshots(
        Dictionary<int, Order> ordersById,
        Dictionary<(int, CurrencyType), decimal> fundSnapshots,
        Dictionary<(int, int), (int Quantity, int Reserved)> posSnapshots,
        Dictionary<int, decimal?> budgetSnapshots)
    {
        foreach (var (orderId, budget) in budgetSnapshots)
            if (ordersById.TryGetValue(orderId, out var o))
                o.BuyBudget = budget;

        foreach (var (key, prevBalance) in fundSnapshots)
        {
            var f = _accounts.GetFund(key.Item1, key.Item2);
            if (f != null) f.TotalBalance = prevBalance;
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

        // Release the unfilled reservation for sells. We use the in-memory `order` here —
        // it carries the up-to-date AmountFilled set by matching, while `dbOrder` was
        // freshly loaded and would not reflect partial fills not yet flushed.
        ReleaseSellReservation(order, order.RemainingQuantity);
    }

    private void ReleaseSellReservation(Order order, int qty)
    {
        if (qty <= 0 || !order.IsSellOrder) return;
        var pos = _accounts.GetPosition(order.UserId, order.StockId);
        if (pos is null) return;
        try { pos.UnreserveStock(qty); }
        catch (ArgumentException) { /* hydration mismatch — swallow defensively */ }
    }

    public async Task ApplyOrderChangeAsync(Order order, int? newQuantity, decimal? newPrice, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Compute reservation delta up front so we can apply it after the tx commits.
        // Sell-only: delta in reservation == delta in Quantity (RemainingQuantity = Quantity − AmountFilled
        // shifts identically with Quantity since AmountFilled is unchanged here).
        int reservationDelta = 0;
        Position? sellPos = null;
        if (newQuantity.HasValue && order.IsSellOrder)
        {
            reservationDelta = newQuantity.Value - order.Quantity;
            if (reservationDelta != 0)
            {
                sellPos = _accounts.GetPosition(order.UserId, order.StockId);
                if (sellPos is null)
                    throw new InvalidOperationException(
                        $"Position not found for seller {order.UserId} on stock {order.StockId}.");
                if (reservationDelta > 0 && sellPos.AvailableQuantity < reservationDelta)
                    throw new InvalidOperationException(
                        $"Insufficient shares to grow sell order (user {order.UserId}).");
            }
        }

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

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        // Apply reservation change only after the tx commits — otherwise we'd have to roll
        // it back on throw, and the throw path above already restores via tx rollback.
        if (sellPos is not null && reservationDelta != 0)
        {
            try
            {
                if (reservationDelta > 0) sellPos.ReserveStock(reservationDelta);
                else sellPos.UnreserveStock(-reservationDelta);
            }
            catch (ArgumentException) { /* defensive — pre-checked above */ }
        }
    }
    #endregion

    #region Helpers
    private static bool IsTrueMarketBuy(Order o) => o.OrderType == Order.Types.TrueMarketBuy;

    private static decimal Round(decimal amount, CurrencyType ccy)
        => CurrencyHelper.RoundMoney(amount, ccy);
    #endregion
}
