using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.PortfolioServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed class OrderExecutionService : IOrderExecutionService
{
    private readonly bool DebugMode = false;

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IOrderBookCache _books;
    private readonly IMatchingEngine _matching;
    private readonly IOrderValidator _validator;
    private readonly ISettlementEngine _settlement;
    private readonly IMarketDataService _marketData;
    private readonly IAccountsCache _accounts;
    private readonly ILogger<OrderExecutionService> _logger;

    public OrderExecutionService(IDataBaseService db, IOrderBookCache books,
        IMatchingEngine matching, IOrderValidator validator, ISettlementEngine settlement,
        IMarketDataService marketData, IAccountsCache accounts,
        ILogger<OrderExecutionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _books = books ?? throw new ArgumentNullException(nameof(books));
        _matching = matching ?? throw new ArgumentNullException(nameof(matching));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Order Execution and Matching
    public async Task<OrderResult> PlaceAndMatchAsync(Order incoming, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Validate incoming order
        var validationError = _validator.ValidateNew(incoming);
        if (validationError != null) return validationError;

        // Balance check and persist order
        var reserveError = await _settlement.SettleOrderAsync(incoming, ct).ConfigureAwait(false);
        if (reserveError != null) return reserveError;

        // Matching & settlement under book lock
        List<Transaction> trades = new();

        await _books.WithBookLockAsync(incoming.StockId, incoming.CurrencyType, ct, async book =>
        {
            var result = _matching.Match(incoming, book, ct);

            // Build ordersById from in-memory objects — no DB reload needed
            var ordersById = BuildOrdersById(incoming, result);

            var settleErr = await _settlement.SettleTradesAsync(result.Fills, ordersById, ct).ConfigureAwait(false);
            if (settleErr != null)
            {
                RollbackMatch(incoming, result, book);
                throw new InvalidOperationException(settleErr.ToString());
            }

            trades.AddRange(result.Fills);

            if (incoming.IsOpenLimitOrder)
                book.UpsertOrder(incoming);
            else if (incoming.IsOpen)
                await _settlement.CancelRemainderAsync(incoming, ct).ConfigureAwait(false);

        }).ConfigureAwait(false);

        // Publish ticks to market data (outside lock)
        if (trades.Count > 0)
            await _marketData.OnTicksAsync(trades, ct).ConfigureAwait(false);

        return OrderResultFactory.Success(incoming, trades);
    }

    public async Task<OrderResult> CancelOrderAsync(int orderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        Order? order = await _db.GetOrderById(orderId, ct);
        if (order == null) return OrderResultFactory.InvalidParams("Order not found.");

        var validation = _validator.ValidateCancel(order);
        if (validation != null) return validation;

        await _books.WithBookLockAsync(order.StockId, order.CurrencyType, ct, async book =>
        {
            book.RemoveById(order.OrderId);
            await _settlement.CancelRemainderAsync(order, ct);
        });

        return OrderResultFactory.Cancelled(order);
    }

    public async Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        Order? order = await _db.GetOrderById(orderId, ct);
        if (order == null) return OrderResultFactory.InvalidParams("Order not found.");

        var validation = _validator.ValidateModify(order, newQuantity, newPrice);
        if (validation != null) return validation;

        List<Transaction> txs = new();

        await _books.WithBookLockAsync(order.StockId, order.CurrencyType, ct, async book =>
        {
            book.RemoveById(order.OrderId);

            await _settlement.ApplyOrderChangeAsync(order, newQuantity, newPrice, ct);

            var result = _matching.Match(order, book, ct);

            var ordersById = BuildOrdersById(order, result);

            var settleErr = await _settlement.SettleTradesAsync(result.Fills, ordersById, ct).ConfigureAwait(false);
            if (settleErr != null)
            {
                RollbackMatch(order, result, book);
                if (order.IsOpen && order.IsLimitOrder)
                    book.UpsertOrder(order);
                throw new InvalidOperationException(settleErr.ToString());
            }
            txs.AddRange(result.Fills);

            if (order.IsOpen && order.IsLimitOrder)
                book.UpsertOrder(order);
            else if (order.IsOpen)
                await _settlement.CancelRemainderAsync(order, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (txs.Count > 0)
            await _marketData.OnTicksAsync(txs, ct).ConfigureAwait(false);

        return OrderResultFactory.Modified(order, txs);
    }

    #endregion

    #region Batch Operations
    public async Task<IReadOnlyList<OrderResult>> PlaceAndMatchBatchAsync(
        IReadOnlyList<Order> orders, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (orders.Count == 0) return Array.Empty<OrderResult>();

        // Phase 1: structural validation — no DB calls
        var results = new OrderResult[orders.Count];
        var validOrders = new List<(int index, Order order)>(orders.Count);

        for (int i = 0; i < orders.Count; i++)
        {
            var err = _validator.ValidateNew(orders[i]);
            if (err != null) results[i] = err;
            else validOrders.Add((i, orders[i]));
        }

        if (validOrders.Count == 0) return results;

        // Per-tx mutation tracking. fund/pos/budget snapshots flow into SettleTradesNoTxAsync
        // so the cache can be rolled back if commit fails. Allocated up front because Phase 1.5
        // also writes into posSnapshots when it reserves position quantity.
        var fundSnapshots = new Dictionary<(int, CurrencyType), decimal>();
        var posSnapshots = new Dictionary<(int, int), (int Quantity, int Reserved)>();
        var budgetSnapshots = new Dictionary<int, decimal?>();
        var pendingNewPositions = new Dictionary<(int, int), Position>();

        // Phase 1.5: pre-flight position check for sell orders. Reads the in-memory account
        // cache rather than the DB — IAccountsCache is the same instance settlement mutates,
        // so it's the live source of truth, not stale like the bot's local context cache was.
        HashSet<int>? sellerIds = null;
        List<(int index, Order order)>? sellOrders = null;
        for (int i = 0; i < validOrders.Count; i++)
        {
            var vo = validOrders[i];
            if (!vo.order.IsSellOrder) continue;
            sellerIds ??= new HashSet<int>();
            sellOrders ??= new List<(int, Order)>();
            sellerIds.Add(vo.order.UserId);
            sellOrders.Add(vo);
        }

        if (sellOrders is not null)
        {
            var sellerIdList = new List<int>(sellerIds!.Count);
            foreach (var id in sellerIds) sellerIdList.Add(id);
            await _accounts.EnsureLoadedAsync(sellerIdList, ct).ConfigureAwait(false);

            // Reserve quantity on each accepted sell order. AvailableQuantity already accounts
            // for the user's prior open sells (hydrated into ReservedQuantity), so a multi-tick
            // over-promise is rejected here. ReserveStock decrements AvailableQuantity in-place,
            // so subsequent sells from the same seller in this batch see the updated value
            // without a parallel running map.
            HashSet<int>? rejected = null;
            for (int i = 0; i < sellOrders.Count; i++)
            {
                var (idx, order) = sellOrders[i];
                var pos = _accounts.GetPosition(order.UserId, order.StockId);
                if (pos is null || pos.AvailableQuantity < order.Quantity)
                {
                    results[idx] = OrderResultFactory.InsufficientStocks(
                        $"Insufficient shares for sell order (user {order.UserId}).");
                    rejected ??= new HashSet<int>();
                    rejected.Add(idx);
                    continue;
                }

                // Snapshot before mutating so RestoreCacheSnapshots can undo on rollback.
                var key = (order.UserId, order.StockId);
                if (pos.PositionId != 0 && !posSnapshots.ContainsKey(key))
                    posSnapshots[key] = (pos.Quantity, pos.ReservedQuantity);

                try { pos.ReserveStock(order.Quantity); }
                catch (ArgumentException)
                {
                    results[idx] = OrderResultFactory.InsufficientStocks(
                        $"Insufficient shares for sell order (user {order.UserId}).");
                    rejected ??= new HashSet<int>();
                    rejected.Add(idx);
                }
            }

            if (rejected is not null)
            {
                int write = 0;
                for (int read = 0; read < validOrders.Count; read++)
                    if (!rejected.Contains(validOrders[read].index))
                        validOrders[write++] = validOrders[read];
                validOrders.RemoveRange(write, validOrders.Count - write);
                if (validOrders.Count == 0)
                {
                    // No survivors — undo the reservations we just took for the rejects'
                    // peers (already-accepted sells from this batch). Easiest way is the
                    // snapshot restore path the rest of the failure flow uses.
                    _settlement.RestoreCacheSnapshots(
                        new Dictionary<int, Order>(), fundSnapshots, posSnapshots, budgetSnapshots);
                    return results;
                }
            }
        }

        // Phases 2 + 3 share one root SQLite transaction. Inserting the orders, matching,
        // and settling per-book all run inside it — one BEGIN, one COMMIT, regardless of
        // how many books the batch touches. On any failure we roll back the tx, undo the
        // in-memory book mutations, and restore cache snapshots.
        var orderList = new List<Order>(validOrders.Count);
        for (int i = 0; i < validOrders.Count; i++) orderList.Add(validOrders[i].order);

        // Group by (stockId, currency) up-front so the in-tx loop knows what to do.
        var groups = new Dictionary<(int, CurrencyType), List<(int index, Order order)>>();
        for (int i = 0; i < validOrders.Count; i++)
        {
            var vo = validOrders[i];
            var key = (vo.order.StockId, vo.order.CurrencyType);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<(int, Order)>();
                groups[key] = list;
            }
            list.Add(vo);
        }

        // groupOutcomes records every match and book upsert so books can be put back to their
        // pre-batch state on rollback. fund/pos/budget snapshots were declared earlier — Phase
        // 1.5 already populated posSnapshots with the seller positions it reserved against.
        var groupOutcomes = new List<GroupOutcome>(groups.Count);
        var allFills = new List<Transaction>();
        var ordersByIdAccum = new Dictionary<int, Order>();

        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // Phase 2: bulk-insert orders inside the tx. AutoIncrement IDs land on the
            // entity instances so subsequent UpdateAllAsync calls hit the right rows.
            await _db.InsertAllAsync(orderList, ct).ConfigureAwait(false);

            // Phase 3: per-book matching + settlement, all under the ambient tx.
            // Sequential — book locks are independent SemaphoreSlims, but the SQLite writer
            // is shared so fan-out wins nothing on the DB side. Fail-fast on first error.
            foreach (var kv in groups)
            {
                var (stockId, currency) = kv.Key;
                var groupItems = kv.Value;
                var outcome = new GroupOutcome { StockId = stockId, Currency = currency };

                await _books.WithBookLockAsync(stockId, currency, ct, async book =>
                {
                    var groupFills = new List<Transaction>();
                    var groupOrdersById = new Dictionary<int, Order>(groupItems.Count * 2);
                    for (int i = 0; i < groupItems.Count; i++)
                    {
                        var (idx, order) = groupItems[i];
                        var match = _matching.Match(order, book, ct);
                        outcome.Records.Add(new MatchRecord(idx, order, match, false));
                        groupFills.AddRange(match.Fills);

                        groupOrdersById[order.OrderId] = order;
                        ordersByIdAccum[order.OrderId] = order;
                        for (int s = 0; s < match.MakerSnapshots.Count; s++)
                        {
                            var maker = match.MakerSnapshots[s].Order;
                            groupOrdersById[maker.OrderId] = maker;
                            ordersByIdAccum[maker.OrderId] = maker;
                        }
                    }

                    if (groupFills.Count > 0)
                    {
                        var settleErr = await _settlement.SettleTradesNoTxAsync(
                            groupFills, groupOrdersById,
                            fundSnapshots, posSnapshots, budgetSnapshots, pendingNewPositions,
                            ct).ConfigureAwait(false);
                        if (settleErr != null)
                            throw new InvalidOperationException(
                                settleErr.ErrorMessage ?? "Settlement failed.");
                    }

                    // Upsert remainders / cancel non-open orders. Both happen inside the
                    // same tx; CancelRemainderAsync's UpdateOrder call piggybacks on it.
                    for (int i = 0; i < outcome.Records.Count; i++)
                    {
                        var rec = outcome.Records[i];
                        if (rec.Order.IsOpenLimitOrder)
                        {
                            book.UpsertOrder(rec.Order);
                            outcome.Records[i] = rec with { Upserted = true };
                        }
                        else if (rec.Order.IsOpen)
                        {
                            await _settlement.CancelRemainderAsync(rec.Order, ct).ConfigureAwait(false);
                        }
                    }

                    allFills.AddRange(groupFills);
                }).ConfigureAwait(false);

                groupOutcomes.Add(outcome);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // After commit: register any positions created during this batch in the cache.
            foreach (var pos in pendingNewPositions.Values)
                _accounts.TrackNewPosition(pos);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex,
                "PlaceAndMatchBatchAsync: root tx failed across {GroupCount} groups; rolling back",
                groups.Count);

            // Roll back every book mutation we performed, in reverse order. Upserts placed
            // the taker on the book, so removing it first leaves only the maker fills to
            // undo via the existing RollbackMatch helper.
            for (int i = groupOutcomes.Count - 1; i >= 0; i--)
            {
                var outcome = groupOutcomes[i];
                try
                {
                    await _books.WithBookLockAsync(outcome.StockId, outcome.Currency, ct, book =>
                    {
                        for (int j = outcome.Records.Count - 1; j >= 0; j--)
                        {
                            var rec = outcome.Records[j];
                            if (rec.Upserted) book.RemoveById(rec.Order.OrderId);
                            RollbackMatch(rec.Order, rec.Match, book);
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    _logger.LogError(inner,
                        "PlaceAndMatchBatchAsync: book rollback failed for ({StockId},{Currency})",
                        outcome.StockId, outcome.Currency);
                }
            }

            // Restore cache snapshots through the settlement engine so the same instances
            // settlement mutated get put back to their pre-batch values. Pending new
            // positions were never registered in the cache, so they simply drop.
            _settlement.RestoreCacheSnapshots(ordersByIdAccum, fundSnapshots, posSnapshots, budgetSnapshots);

            for (int i = 0; i < validOrders.Count; i++)
                results[validOrders[i].index] = OrderResultFactory.OperationFailed(
                    $"Batch settlement failed: {ex.Message}");
            return results;
        }

        // Phase 4: publish ticks outside all locks (coalesced across books).
        if (allFills.Count > 0)
            await _marketData.OnTicksAsync(allFills, ct).ConfigureAwait(false);

        // Mark every successful order with its result.
        for (int gi = 0; gi < groupOutcomes.Count; gi++)
        {
            var outcome = groupOutcomes[gi];
            for (int i = 0; i < outcome.Records.Count; i++)
            {
                var rec = outcome.Records[i];
                results[rec.Index] = OrderResultFactory.Success(rec.Order, rec.Match.Fills);
            }
        }
        return results;
    }

    private sealed record MatchRecord(int Index, Order Order, MatchResult Match, bool Upserted);

    private sealed class GroupOutcome
    {
        public int StockId;
        public CurrencyType Currency;
        public List<MatchRecord> Records { get; } = new();
    }

    public async Task<IReadOnlyList<OrderResult>> CancelOrdersBatchAsync(
        IReadOnlyList<int> orderIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (orderIds is null || orderIds.Count == 0) return Array.Empty<OrderResult>();

        var idList = new List<int>(orderIds);
        var dbOrders = await _db.GetOrdersByIds(idList, ct).ConfigureAwait(false);

        var byId = new Dictionary<int, Order>(dbOrders.Count);
        for (int i = 0; i < dbOrders.Count; i++) byId[dbOrders[i].OrderId] = dbOrders[i];

        var results = new OrderResult[orderIds.Count];
        var toCancel = new List<Order>(orderIds.Count);
        var resultIdxByOrderId = new Dictionary<int, int>(orderIds.Count);

        for (int i = 0; i < orderIds.Count; i++)
        {
            var id = orderIds[i];
            if (!byId.TryGetValue(id, out var o))
            {
                results[i] = OrderResultFactory.InvalidParams("Order not found.");
                continue;
            }

            var validation = _validator.ValidateCancel(o);
            if (validation != null)
            {
                results[i] = validation;
                continue;
            }

            toCancel.Add(o);
            resultIdxByOrderId[o.OrderId] = i;
        }

        if (toCancel.Count == 0) return results;

        // Hand-rolled grouping; avoids LINQ allocations on a path that the bot prune timer hits.
        var groups = new Dictionary<(int, CurrencyType), List<Order>>();
        for (int i = 0; i < toCancel.Count; i++)
        {
            var o = toCancel[i];
            var key = (o.StockId, o.CurrencyType);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<Order>();
                groups[key] = list;
            }
            list.Add(o);
        }

        // One root tx around the in-memory book updates and the DB write. Each book lock is
        // taken sequentially — they're independent SemaphoreSlims, but the underlying SQLite
        // connection serializes anyway, so parallel acquire would buy nothing.
        await using var tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var kv in groups)
            {
                var (stockId, currency) = kv.Key;
                var list = kv.Value;
                await _books.WithBookLockAsync(stockId, currency, ct, book =>
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var o = list[i];
                        book.RemoveById(o.OrderId);
                        if (o.IsOpen) o.Cancel();
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            await _db.UpdateAllAsync(toCancel, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex, "CancelOrdersBatchAsync: tx failed for {Count} orders", toCancel.Count);

            // Restore book state for the orders we removed; status mutation on the in-memory
            // Order is unchanged since we only flipped Cancel on the same instance, but the
            // book lost its reference. Upsert puts it back at the (price, side) tail.
            foreach (var kv in groups)
            {
                var (stockId, currency) = kv.Key;
                var list = kv.Value;
                try
                {
                    await _books.WithBookLockAsync(stockId, currency, ct, book =>
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var o = list[i];
                            // Cancel() flipped status; restore Open so UpsertOrder accepts it.
                            if (!o.IsOpen) o.Status = Order.Statuses.Open;
                            if (o.IsOpenLimitOrder) book.UpsertOrder(o);
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (Exception inner)
                {
                    _logger.LogError(inner,
                        "CancelOrdersBatchAsync: book restore failed for ({StockId},{Currency})",
                        stockId, currency);
                }
            }

            for (int i = 0; i < toCancel.Count; i++)
            {
                if (resultIdxByOrderId.TryGetValue(toCancel[i].OrderId, out var idx))
                    results[idx] = OrderResultFactory.OperationFailed(
                        $"Cancel batch failed: {ex.Message}");
            }
            return results;
        }

        // Tx committed — release the reservations held by cancelled sell orders. Done
        // post-commit so the failure path doesn't have to undo reservation releases.
        for (int i = 0; i < toCancel.Count; i++)
        {
            var o = toCancel[i];
            if (!o.IsSellOrder) continue;
            var unreserve = o.Quantity - o.AmountFilled;
            if (unreserve <= 0) continue;
            var pos = _accounts.GetPosition(o.UserId, o.StockId);
            if (pos is null) continue;
            try { pos.UnreserveStock(unreserve); }
            catch (ArgumentException) { /* hydration mismatch — swallow defensively */ }
        }

        for (int i = 0; i < toCancel.Count; i++)
        {
            if (resultIdxByOrderId.TryGetValue(toCancel[i].OrderId, out var idx))
                results[idx] = OrderResultFactory.Cancelled(toCancel[i]);
        }
        return results;
    }
    #endregion

    #region Helpers
    private static Dictionary<int, Order> BuildOrdersById(Order taker, MatchResult result)
    {
        var map = new Dictionary<int, Order>(result.MakerSnapshots.Count + 1);
        map[taker.OrderId] = taker;
        foreach (var snap in result.MakerSnapshots)
            map[snap.Order.OrderId] = snap.Order;
        return map;
    }

    /// <summary>
    /// Undoes the in-memory side-effects of a Match call when DB settlement has been rolled back.
    /// Restores fill state on the taker and all touched makers, and uses the book's
    /// rollback path to keep per-level totals + index consistent.
    /// </summary>
    private static void RollbackMatch(Order taker, MatchResult result, OrderBook book)
    {
        taker.AmountFilled = result.TakerOriginalFilled;
        taker.Status = Order.Statuses.Open;

        foreach (var snap in result.MakerSnapshots)
        {
            // ApplyMakerFill mutated AmountFilled by (currentFilled - snap.OriginalAmountFilled).
            // Capture that delta before restoring AmountFilled so the book can credit the
            // corresponding qty back to the level total.
            var filledDelta = snap.Order.AmountFilled - snap.OriginalAmountFilled;
            snap.Order.AmountFilled = snap.OriginalAmountFilled;
            snap.Order.Status = Order.Statuses.Open;
            book.RollbackMakerFill(snap.Order, filledDelta, snap.WasRemovedFromBook);
        }
    }
    #endregion
}
