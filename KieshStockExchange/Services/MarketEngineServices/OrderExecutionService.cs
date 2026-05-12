using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;

namespace KieshStockExchange.Services.MarketEngineServices;

public sealed class OrderExecutionService : IOrderExecutionService
{
    private readonly bool DebugMode = false;

    // Diagnostic filter for the high-volume "Cancelled stale maker order" warning
    // below. With 20k+ bots, stale-maker cancellations fire constantly and bury
    // everything else. Setting DebugUserId pins the log to a single user (admin)
    // so only their order cancellations are visible. Set to null to show all.
    private readonly int? DebugUserId = 20001;

    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IOrderBookCache _books;
    private readonly IMatchingEngine _matching;
    private readonly IOrderValidator _validator;
    private readonly ISettlementEngine _settlement;
    private readonly IMarketDataService _marketData;
    private readonly IAccountsCache _accounts;
    private readonly IOrderCacheService _orderCache;
    private readonly ILogger<OrderExecutionService> _logger;

    public OrderExecutionService(IDataBaseService db, IOrderBookCache books,
        IMatchingEngine matching, IOrderValidator validator, ISettlementEngine settlement,
        IMarketDataService marketData, IAccountsCache accounts,
        IOrderCacheService orderCache,
        ILogger<OrderExecutionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _books = books ?? throw new ArgumentNullException(nameof(books));
        _matching = matching ?? throw new ArgumentNullException(nameof(matching));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _settlement = settlement ?? throw new ArgumentNullException(nameof(settlement));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _orderCache = orderCache ?? throw new ArgumentNullException(nameof(orderCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Build the set of user ids whose orders changed in this settlement so the
    /// active-user order cache can refresh in real time (a maker fill on user A's
    /// resting order should clear that row from A's UI without waiting for the
    /// minute-boundary portfolio poll).
    /// </summary>
    private static HashSet<int> CollectAffectedUsers(Order taker, IReadOnlyDictionary<int, Order> ordersById)
    {
        var set = new HashSet<int>(ordersById.Count + 1) { taker.UserId };
        foreach (var o in ordersById.Values) set.Add(o.UserId);
        return set;
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
        HashSet<int>? affectedUsers = null;

        // Why the book lock is held across the DB settlement call (not released between
        // Match and SettleTrades): MatchingEngine.Match mutates the book in-memory.
        // RollbackMatch on settlement failure assumes the book hasn't moved since the
        // match. Releasing the lock between the two steps would let a concurrent order
        // edit the same levels, breaking rollback. The DB writer is serial anyway, so
        // releasing earlier wouldn't help throughput.
        await _books.WithBookLockAsync(incoming.StockId, incoming.CurrencyType, ct, async book =>
        {
            var result = _matching.Match(incoming, book, ct);

            // Build ordersById from in-memory objects — no DB reload needed
            var ordersById = BuildOrdersById(incoming, result);

            var (settleErr, rejected) = await _settlement.SettleTradesAsync(result.Fills, ordersById, ct).ConfigureAwait(false);
            if (settleErr != null)
            {
                RollbackMatch(incoming, result, book);
                throw new InvalidOperationException(settleErr.ToString());
            }

            // Cancel makers that couldn't honor their fills + roll back their per-fill effect.
            // The single-order path's apply-pass tx has already committed — so we issue a
            // separate UpdateAllAsync for the cancelled makers. (In the batch path, this
            // happens inside the still-open root tx instead.)
            if (rejected.Count > 0)
            {
                RollbackRejectedFills(new[] { (incoming, result) }, book, rejected, ordersById);
                var cancelled = new List<Order>(rejected.Count);
                for (int i = 0; i < rejected.Count; i++)
                {
                    if (ordersById.TryGetValue(rejected[i].MakerOrderId, out var maker)
                        && maker.Status == Order.Statuses.Cancelled)
                        cancelled.Add(maker);
                }
                if (cancelled.Count > 0)
                    await _db.UpdateAllAsync(cancelled, ct).ConfigureAwait(false);
            }

            // Publish accepted fills only (rejected ones never happened).
            if (rejected.Count == 0)
                trades.AddRange(result.Fills);
            else
            {
                var rejectedSet = new HashSet<Transaction>(ReferenceEqualityComparer.Instance);
                foreach (var rj in rejected) rejectedSet.Add(rj.Trade);
                for (int i = 0; i < result.Fills.Count; i++)
                    if (!rejectedSet.Contains(result.Fills[i])) trades.Add(result.Fills[i]);
            }

            if (incoming.IsOpenLimitOrder)
                book.UpsertOrder(incoming);
            else if (incoming.IsOpen)
                await _settlement.CancelRemainderAsync(incoming, ct).ConfigureAwait(false);

            // Snapshot affected users before lock releases. The cache notify happens
            // outside the lock so the fire-and-forget RefreshAsync doesn't extend the
            // critical section.
            affectedUsers = CollectAffectedUsers(incoming, ordersById);

        }).ConfigureAwait(false);

        // Publish ticks to market data (outside lock)
        if (trades.Count > 0)
            await _marketData.OnTicksAsync(trades, ct).ConfigureAwait(false);

        // Refresh the order cache for the active user if they're in the affected set.
        // No-op when the cache hasn't been refreshed yet (cold start) or when the
        // active user isn't one of the touched users.
        if (affectedUsers is not null)
            _orderCache.NotifyOrdersMutated(affectedUsers);

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

        _orderCache.NotifyOrdersMutated(new[] { order.UserId });
        return OrderResultFactory.Cancelled(order);
    }

    public async Task<OrderResult> ModifyOrderAsync(int orderId, int? newQuantity = null,
        decimal? newPrice = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Diagnostic: ModifyOrderAsync was observed appearing hung under heavy bot
        // batch load. Entry/exit logs gated by DebugUserId let us see whether the
        // call enters/exits the engine at all, and how long it takes when it does.
        // Pair with the _writeGate wait warning in LocalDBService to attribute the
        // delay to gate contention vs book lock / load gate / something else.
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Order? order = await _db.GetOrderById(orderId, ct);
        if (order == null)
        {
            if (DebugMode)
                _logger.LogInformation(
                    "ModifyOrderAsync EXIT order #{OrderId} not found ({ElapsedMs}ms).",
                    orderId, sw.ElapsedMilliseconds);
            return OrderResultFactory.InvalidParams("Order not found.");
        }

        bool trace = !DebugUserId.HasValue || order.UserId == DebugUserId.Value;
        if (trace)
            _logger.LogInformation(
                "ModifyOrderAsync ENTRY order #{OrderId} user {UserId} newQty={NewQty} newPx={NewPx}.",
                orderId, order.UserId, newQuantity, newPrice);

        var validation = _validator.ValidateModify(order, newQuantity, newPrice);
        if (validation != null)
        {
            if (trace)
                _logger.LogInformation(
                    "ModifyOrderAsync EXIT order #{OrderId} rejected by validator ({ElapsedMs}ms).",
                    orderId, sw.ElapsedMilliseconds);
            return validation;
        }

        List<Transaction> txs = new();
        HashSet<int>? affectedUsers = null;

        try
        {
            await _books.WithBookLockAsync(order.StockId, order.CurrencyType, ct, async book =>
            {
                book.RemoveById(order.OrderId);

                await _settlement.ApplyOrderChangeAsync(order, newQuantity, newPrice, ct);

                var result = _matching.Match(order, book, ct);

                var ordersById = BuildOrdersById(order, result);

                var (settleErr, rejected) = await _settlement.SettleTradesAsync(result.Fills, ordersById, ct).ConfigureAwait(false);
                if (settleErr != null)
                {
                    RollbackMatch(order, result, book);
                    if (order.IsOpen && order.IsLimitOrder)
                        book.UpsertOrder(order);
                    throw new InvalidOperationException(settleErr.ToString());
                }

                if (rejected.Count > 0)
                {
                    RollbackRejectedFills(new[] { (order, result) }, book, rejected, ordersById);
                    var cancelled = new List<Order>(rejected.Count);
                    for (int i = 0; i < rejected.Count; i++)
                    {
                        if (ordersById.TryGetValue(rejected[i].MakerOrderId, out var maker)
                            && maker.Status == Order.Statuses.Cancelled)
                            cancelled.Add(maker);
                    }
                    if (cancelled.Count > 0)
                        await _db.UpdateAllAsync(cancelled, ct).ConfigureAwait(false);
                }

                if (rejected.Count == 0)
                    txs.AddRange(result.Fills);
                else
                {
                    var rejectedSet = new HashSet<Transaction>(ReferenceEqualityComparer.Instance);
                    foreach (var rj in rejected) rejectedSet.Add(rj.Trade);
                    for (int i = 0; i < result.Fills.Count; i++)
                        if (!rejectedSet.Contains(result.Fills[i])) txs.Add(result.Fills[i]);
                }

                if (order.IsOpen && order.IsLimitOrder)
                    book.UpsertOrder(order);
                else if (order.IsOpen)
                    await _settlement.CancelRemainderAsync(order, ct).ConfigureAwait(false);

                affectedUsers = CollectAffectedUsers(order, ordersById);
            }).ConfigureAwait(false);
        }
        catch
        {
            if (trace)
                _logger.LogWarning(
                    "ModifyOrderAsync EXIT order #{OrderId} threw after {ElapsedMs}ms.",
                    orderId, sw.ElapsedMilliseconds);
            throw;
        }

        if (txs.Count > 0)
            await _marketData.OnTicksAsync(txs, ct).ConfigureAwait(false);

        if (affectedUsers is not null)
            _orderCache.NotifyOrdersMutated(affectedUsers);

        if (trace)
            _logger.LogInformation(
                "ModifyOrderAsync EXIT order #{OrderId} OK fills={Fills} ({ElapsedMs}ms).",
                orderId, txs.Count, sw.ElapsedMilliseconds);

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
        // also writes into posSnapshots and fundSnapshots when it reserves stock and funds.
        var fundSnapshots = new Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)>();
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
                // Enriched error messages: include the engine's view (Quantity, Reserved,
                // Available) alongside what the order needs so the failure CSV reveals
                // whether the bot's ctx Quantity is stale (engine has fewer total shares)
                // or whether ReservedQuantity is over-committed (engine has shares but
                // they're locked up elsewhere). Without these numbers the message just
                // said "Insufficient shares" with no way to triage divergence.
                if (pos is null)
                {
                    results[idx] = OrderResultFactory.InsufficientStocks(
                        $"Insufficient shares for sell order (user {order.UserId}, stock {order.StockId}): no position row.");
                    rejected ??= new HashSet<int>();
                    rejected.Add(idx);
                    continue;
                }
                if (pos.AvailableQuantity < order.Quantity)
                {
                    results[idx] = OrderResultFactory.InsufficientStocks(
                        $"Insufficient shares for sell order (user {order.UserId}, stock {order.StockId}): " +
                        $"needs {order.Quantity}, available {pos.AvailableQuantity} " +
                        $"(Quantity={pos.Quantity}, Reserved={pos.ReservedQuantity}).");
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
                    // Race: AvailableQuantity changed between the check above and the
                    // ReserveStock call. Re-read so the failure message reflects the
                    // value ReserveStock actually saw.
                    results[idx] = OrderResultFactory.InsufficientStocks(
                        $"Insufficient shares for sell order (user {order.UserId}, stock {order.StockId}): " +
                        $"race on ReserveStock, needs {order.Quantity}, available {pos.AvailableQuantity} " +
                        $"(Quantity={pos.Quantity}, Reserved={pos.ReservedQuantity}).");
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

        // Phase 1.6: pre-flight fund check + reservation for buy orders. Mirrors the seller
        // pre-flight above. AvailableBalance accounts for the user's prior open buys (now
        // hydrated into ReservedBalance), so multi-order over-promise is rejected here
        // instead of at settlement.
        HashSet<int>? buyerIds = null;
        List<(int index, Order order)>? buyOrders = null;
        for (int i = 0; i < validOrders.Count; i++)
        {
            var vo = validOrders[i];
            if (!vo.order.IsBuyOrder) continue;
            buyerIds ??= new HashSet<int>();
            buyOrders ??= new List<(int, Order)>();
            buyerIds.Add(vo.order.UserId);
            buyOrders.Add(vo);
        }

        if (buyOrders is not null)
        {
            var buyerIdList = new List<int>(buyerIds!.Count);
            foreach (var id in buyerIds) buyerIdList.Add(id);
            await _accounts.EnsureLoadedAsync(buyerIdList, ct).ConfigureAwait(false);

            HashSet<int>? buyRejected = null;
            for (int i = 0; i < buyOrders.Count; i++)
            {
                var (idx, order) = buyOrders[i];
                var fund = _accounts.GetFund(order.UserId, order.CurrencyType);
                var reservation = SettlementEngine.InitialBuyReservation(order);
                if (fund is null)
                {
                    results[idx] = OrderResultFactory.InsufficientFunds(
                        $"Insufficient funds for buy order (user {order.UserId}, {order.CurrencyType}): no fund row.");
                    buyRejected ??= new HashSet<int>();
                    buyRejected.Add(idx);
                    continue;
                }
                if (reservation <= 0m)
                {
                    results[idx] = OrderResultFactory.InsufficientFunds(
                        $"Insufficient funds for buy order (user {order.UserId}, {order.CurrencyType}): " +
                        $"computed reservation is {reservation} (price={order.Price}, qty={order.Quantity}).");
                    buyRejected ??= new HashSet<int>();
                    buyRejected.Add(idx);
                    continue;
                }
                if (fund.AvailableBalance < reservation)
                {
                    results[idx] = OrderResultFactory.InsufficientFunds(
                        $"Insufficient funds for buy order (user {order.UserId}, {order.CurrencyType}): " +
                        $"needs {reservation}, available {fund.AvailableBalance} " +
                        $"(Total={fund.TotalBalance}, Reserved={fund.ReservedBalance}).");
                    buyRejected ??= new HashSet<int>();
                    buyRejected.Add(idx);
                    continue;
                }

                var fundKey = (order.UserId, order.CurrencyType);
                if (!fundSnapshots.ContainsKey(fundKey))
                    fundSnapshots[fundKey] = (fund.TotalBalance, fund.ReservedBalance);

                try { fund.ReserveFunds(reservation); }
                catch (ArgumentException)
                {
                    results[idx] = OrderResultFactory.InsufficientFunds(
                        $"Insufficient funds for buy order (user {order.UserId}, {order.CurrencyType}): " +
                        $"race on ReserveFunds, needs {reservation}, available {fund.AvailableBalance} " +
                        $"(Total={fund.TotalBalance}, Reserved={fund.ReservedBalance}).");
                    buyRejected ??= new HashSet<int>();
                    buyRejected.Add(idx);
                }
            }

            if (buyRejected is not null)
            {
                int write = 0;
                for (int read = 0; read < validOrders.Count; read++)
                    if (!buyRejected.Contains(validOrders[read].index))
                        validOrders[write++] = validOrders[read];
                validOrders.RemoveRange(write, validOrders.Count - write);
                if (validOrders.Count == 0)
                {
                    _settlement.RestoreCacheSnapshots(
                        new Dictionary<int, Order>(), fundSnapshots, posSnapshots, budgetSnapshots);
                    return results;
                }
            }
        }

        // Section 1b: split the single root tx that used to wrap Phase 2 + every Phase 3
        // group into (a) a short Phase 2 tx that only does the bulk InsertAllAsync and (b)
        // one independent root tx per book group. _writeGate is released between groups so
        // a user-issued modify/cancel/place doesn't queue behind the entire batch.
        //
        // Cross-group atomicity is intentionally lost: if group G fails after groups A+B
        // committed, A's and B's orders stay filled. Acceptable because each group is
        // already independently atomic, the matcher is deterministic per book, and the
        // caller (AiTradeService) reads OrderResult per order — there's no batch-level
        // rollback signal anyone depends on.
        var orderList = new List<Order>(validOrders.Count);
        for (int i = 0; i < validOrders.Count; i++) orderList.Add(validOrders[i].order);

        // Group by (stockId, currency) up-front so each group's tx scope is known.
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

        // Phase 2: short tx — bulk-insert all valid orders so each one has its
        // AutoIncrement OrderId before any matcher runs. If this commit fails, restore the
        // Phase 1.5/1.6 cache reservations and abort the entire batch — none of the groups
        // get to run because the matcher needs OrderIds to populate Transaction rows.
        try
        {
            await using var phase2Tx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await _db.InsertAllAsync(orderList, ct).ConfigureAwait(false);
                await phase2Tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await phase2Tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaceAndMatchBatchAsync: Phase 2 InsertAllAsync failed");
            _settlement.RestoreCacheSnapshots(
                new Dictionary<int, Order>(), fundSnapshots, posSnapshots, budgetSnapshots);
            for (int i = 0; i < validOrders.Count; i++)
                results[validOrders[i].index] = OrderResultFactory.OperationFailed(
                    $"Bulk insert failed: {ex.Message}");
            return results;
        }

        // Phase 3: one root tx per group. Sequential — book locks are independent
        // semaphores, but the SQLite writer is shared so we'd serialise anyway.
        var allFills = new List<Transaction>();
        foreach (var kv in groups)
        {
            var (stockId, currency) = kv.Key;
            var groupItems = kv.Value;

            try
            {
                var groupFills = await RunGroupTxAsync(
                    stockId, currency, groupItems, results, ct).ConfigureAwait(false);
                if (groupFills.Count > 0) allFills.AddRange(groupFills);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PlaceAndMatchBatchAsync: group ({StockId},{Currency}) failed; releasing Phase 1.5/1.6 reservations",
                    stockId, currency);

                // The group tx already rolled back any settle DB writes. The orders for
                // this group are still in DB (inserted in Phase 2) but they aren't on the
                // book and the engine doesn't know about them. Cancel them and release
                // the Phase 1.5/1.6 reservations they consumed so the books don't get
                // ghost open rows that the bot's next refresh treats as still-pending.
                await RecoverFailedGroupAsync(groupItems, results, ex.Message, ct).ConfigureAwait(false);
            }
        }

        // Phase 4: publish ticks outside all locks. Per-group NotifyOrdersMutated already
        // fired inside RunGroupTxAsync; this is the cross-group coalesced tick publish.
        if (allFills.Count > 0)
            await _marketData.OnTicksAsync(allFills, ct).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Run one book group's matcher + settlement inside its own root tx. Mutations
    /// committed here are durable independently of any other group's outcome. Throws on
    /// settlement failure; the caller is responsible for the recovery path (which marks
    /// the group's orders Cancelled and releases their Phase 1.5/1.6 reservations).
    /// </summary>
    private async Task<List<Transaction>> RunGroupTxAsync(
        int stockId, CurrencyType currency,
        List<(int index, Order order)> groupItems,
        OrderResult[] results,
        CancellationToken ct)
    {
        // Fresh snapshot dicts for THIS group only. Restoring them on failure rolls back
        // the settle-pass mutations; Phase 1.5/1.6 reservations are recovered separately
        // by RecoverFailedGroupAsync because they were taken outside any tx.
        var groupFundSnapshots = new Dictionary<(int, CurrencyType), (decimal Total, decimal Reserved)>();
        var groupPosSnapshots = new Dictionary<(int, int), (int Quantity, int Reserved)>();
        var groupBudgetSnapshots = new Dictionary<int, decimal?>();
        var groupPendingNewPositions = new Dictionary<(int, int), Position>();

        var outcome = new GroupOutcome { StockId = stockId, Currency = currency };
        var groupFills = new List<Transaction>();
        var groupOrdersById = new Dictionary<int, Order>(groupItems.Count * 2);

        bool committed = false;
        await _books.WithBookLockAsync(stockId, currency, ct, async book =>
        {
            // Match every item in this group while we hold the book lock. The book is
            // mutated in-memory; if the group tx later rolls back we undo via
            // RollbackMatch in reverse order.
            for (int i = 0; i < groupItems.Count; i++)
            {
                var (idx, order) = groupItems[i];
                var match = _matching.Match(order, book, ct);
                outcome.Records.Add(new MatchRecord(idx, order, match, false));
                groupFills.AddRange(match.Fills);

                groupOrdersById[order.OrderId] = order;
                for (int s = 0; s < match.MakerSnapshots.Count; s++)
                {
                    var maker = match.MakerSnapshots[s].Order;
                    groupOrdersById[maker.OrderId] = maker;
                }
            }

            await using var groupTx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                if (groupFills.Count > 0)
                {
                    var (settleErr, rejected) = await _settlement.SettleTradesNoTxAsync(
                        groupFills, groupOrdersById,
                        groupFundSnapshots, groupPosSnapshots, groupBudgetSnapshots,
                        groupPendingNewPositions, ct).ConfigureAwait(false);
                    if (settleErr != null)
                        throw new InvalidOperationException(
                            settleErr.ErrorMessage ?? "Settlement failed.");

                    if (rejected.Count > 0)
                    {
                        var pairs = new (Order, MatchResult)[outcome.Records.Count];
                        for (int r = 0; r < outcome.Records.Count; r++)
                            pairs[r] = (outcome.Records[r].Order, outcome.Records[r].Match);
                        RollbackRejectedFills(pairs, book, rejected, groupOrdersById);

                        var cancelled = new List<Order>(rejected.Count);
                        for (int i = 0; i < rejected.Count; i++)
                        {
                            if (groupOrdersById.TryGetValue(rejected[i].MakerOrderId, out var maker)
                                && maker.Status == Order.Statuses.Cancelled)
                                cancelled.Add(maker);
                        }
                        if (cancelled.Count > 0)
                            await _db.UpdateAllAsync(cancelled, ct).ConfigureAwait(false);

                        var rejectedSet = new HashSet<Transaction>(ReferenceEqualityComparer.Instance);
                        foreach (var rj in rejected) rejectedSet.Add(rj.Trade);
                        groupFills.RemoveAll(rejectedSet.Contains);
                        for (int r = 0; r < outcome.Records.Count; r++)
                            outcome.Records[r].Match.Fills.RemoveAll(rejectedSet.Contains);
                    }
                }

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

                await groupTx.CommitAsync(ct).ConfigureAwait(false);
                committed = true;
            }
            catch
            {
                await groupTx.RollbackAsync(ct).ConfigureAwait(false);

                // Undo book mutations recorded this group, in reverse order. Upserts come
                // off first so RollbackMatch only has to put maker fills back.
                for (int j = outcome.Records.Count - 1; j >= 0; j--)
                {
                    var rec = outcome.Records[j];
                    if (rec.Upserted) book.RemoveById(rec.Order.OrderId);
                    RollbackMatch(rec.Order, rec.Match, book);
                }
                _settlement.RestoreCacheSnapshots(
                    groupOrdersById, groupFundSnapshots, groupPosSnapshots, groupBudgetSnapshots);
                throw;
            }
        }).ConfigureAwait(false);

        if (!committed) return groupFills; // unreachable in practice — throw above covers it

        // After commit: register newly-created positions in the cache and fire the order
        // cache notify for this group's affected users. Per-group notifies replace the
        // single batch-wide notify so the UI gets smaller, more frequent pushes.
        foreach (var pos in groupPendingNewPositions.Values)
            _accounts.TrackNewPosition(pos);

        var groupAffected = new HashSet<int>();
        for (int i = 0; i < outcome.Records.Count; i++)
        {
            var rec = outcome.Records[i];
            groupAffected.Add(rec.Order.UserId);
            for (int s = 0; s < rec.Match.MakerSnapshots.Count; s++)
                groupAffected.Add(rec.Match.MakerSnapshots[s].Order.UserId);
            results[rec.Index] = OrderResultFactory.Success(rec.Order, rec.Match.Fills);
        }
        if (groupAffected.Count > 0)
            _orderCache.NotifyOrdersMutated(groupAffected);

        return groupFills;
    }

    /// <summary>
    /// Group tx failed after Phase 2 already committed the group's orders. Mark them
    /// Cancelled in DB, release the Phase 1.5/1.6 reservations they consumed, and stamp
    /// OperationFailed in the results array. Best-effort: any error here is logged
    /// rather than rethrown, because the batch already failed for these items and we
    /// don't want to mask the original failure cause.
    /// </summary>
    private async Task RecoverFailedGroupAsync(
        List<(int index, Order order)> groupItems,
        OrderResult[] results,
        string failureReason,
        CancellationToken ct)
    {
        try
        {
            await using var recoveryTx = await _db.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var toUpdate = new List<Order>(groupItems.Count);
                for (int i = 0; i < groupItems.Count; i++)
                {
                    var (idx, order) = groupItems[i];
                    if (order.IsOpen) order.Cancel();
                    toUpdate.Add(order);

                    if (order.IsSellOrder)
                    {
                        var pos = _accounts.GetPosition(order.UserId, order.StockId);
                        if (pos is not null)
                        {
                            var toRelease = Math.Min(order.Quantity, pos.ReservedQuantity);
                            if (toRelease > 0)
                            {
                                pos.UnreserveStock(toRelease);
                                pos.UpdatedAt = TimeHelper.NowUtc();
                            }
                            await _db.UpdateAllAsync(new[] { pos }, ct).ConfigureAwait(false);
                        }
                    }
                    else if (order.IsBuyOrder)
                    {
                        var fund = _accounts.GetFund(order.UserId, order.CurrencyType);
                        var reservation = SettlementEngine.InitialBuyReservation(order);
                        if (fund is not null && reservation > 0m)
                        {
                            var toRelease = Math.Min(reservation, fund.ReservedBalance);
                            if (toRelease > 0m)
                            {
                                fund.UnreserveFunds(toRelease);
                                fund.UpdatedAt = TimeHelper.NowUtc();
                            }
                            await _db.UpdateAllAsync(new[] { fund }, ct).ConfigureAwait(false);
                        }
                    }

                    results[idx] = OrderResultFactory.OperationFailed(
                        $"Batch settlement failed: {failureReason}");
                }
                if (toUpdate.Count > 0)
                    await _db.UpdateAllAsync(toUpdate, ct).ConfigureAwait(false);

                await recoveryTx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await recoveryTx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PlaceAndMatchBatchAsync: recovery tx failed for failed group; orders may be in inconsistent state");
            for (int i = 0; i < groupItems.Count; i++)
                results[groupItems[i].index] = OrderResultFactory.OperationFailed(
                    $"Batch settlement failed and recovery failed: {ex.Message}");
        }
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

        // Tx committed — release the reservations held by cancelled orders. Done
        // post-commit so the failure path doesn't have to undo reservation releases.
        //
        // Race tolerance: another path (RollbackRejectedFills via Section 5a, or the
        // per-group CancelRemainderAsync inside RunGroupTxAsync) can cancel the same
        // order and release its reservation before our post-commit loop runs. We
        // fetched the order's DB row before our tx, so by the time we got here the
        // peer release may already have happened. Detect that by comparing the live
        // reservation against the amount we'd unreserve, and skip rather than throw
        // — over-unreserving would corrupt the cache, and a stack trace per skipped
        // order would bury everything else in the log.
        for (int i = 0; i < toCancel.Count; i++)
        {
            var o = toCancel[i];
            try
            {
                if (o.IsSellOrder)
                {
                    var unreserve = o.Quantity - o.AmountFilled;
                    if (unreserve <= 0) continue;
                    var pos = _accounts.GetPosition(o.UserId, o.StockId);
                    if (pos is null) continue;
                    if (pos.ReservedQuantity < unreserve)
                    {
                        _logger.LogDebug(
                            "CancelOrdersBatchAsync: order #{OrderId} reservation already released " +
                            "by a peer path (reserved={Reserved}, would unreserve={Unreserve}); skipping.",
                            o.OrderId, pos.ReservedQuantity, unreserve);
                        continue;
                    }
                    pos.UnreserveStock(unreserve);
                }
                else if (o.IsBuyOrder)
                {
                    var unreserve = SettlementEngine.RemainingBuyReservation(o);
                    if (unreserve <= 0m) continue;
                    var fund = _accounts.GetFund(o.UserId, o.CurrencyType);
                    if (fund is null) continue;
                    if (fund.ReservedBalance < unreserve)
                    {
                        _logger.LogDebug(
                            "CancelOrdersBatchAsync: order #{OrderId} fund reservation already released " +
                            "by a peer path (reserved={Reserved}, would unreserve={Unreserve}); skipping.",
                            o.OrderId, fund.ReservedBalance, unreserve);
                        continue;
                    }
                    fund.UnreserveFunds(unreserve);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CancelOrdersBatchAsync: failed to release reservation for order #{OrderId} (user {UserId}); continuing.",
                    o.OrderId, o.UserId);
            }
        }

        for (int i = 0; i < toCancel.Count; i++)
        {
            if (resultIdxByOrderId.TryGetValue(toCancel[i].OrderId, out var idx))
                results[idx] = OrderResultFactory.Cancelled(toCancel[i]);
        }

        // Refresh the active user's order cache if any cancelled orders belonged
        // to them. Bot prune cancels rarely touch the active user, so the inner
        // HashSet stays small.
        if (toCancel.Count > 0)
        {
            var affected = new HashSet<int>(toCancel.Count);
            for (int i = 0; i < toCancel.Count; i++) affected.Add(toCancel[i].UserId);
            _orderCache.NotifyOrdersMutated(affected);
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
    /// Recovery for fills the seller can't honor. Aggregates across all MatchResults in the
    /// group (a maker partially filled by taker A and then fully filled by taker B is rolled
    /// back once with the cumulative delta), cancels each offending maker, removes from book,
    /// and reduces each taker's <c>AmountFilled</c> by the rejected qty for that taker.
    /// Cancelled makers are added to <paramref name="ordersById"/> so the caller can persist
    /// them in the same root tx.
    /// </summary>
    /// <summary>Aggregated per-maker state collected by the rollback walk. Replaces
    /// four parallel dictionaries (earliestSnap, anyRemoved, rejectedQty, reason) with
    /// a single keyed lookup so the GC pressure on the per-batch hot path scales with
    /// distinct makers, not 4 × distinct makers.</summary>
    private struct MakerRollback
    {
        public MakerSnapshot EarliestSnap;
        public bool AnyRemovedFromBook;
        public int RejectedQty;
        public string Reason;
    }

    private void RollbackRejectedFills(
        IReadOnlyList<(Order Taker, MatchResult Match)> matches,
        OrderBook book,
        IReadOnlyList<RejectedFill> rejected,
        Dictionary<int, Order> ordersById)
    {
        if (rejected.Count == 0) return;

        // Per-maker aggregates: earliest snapshot (smallest OriginalAmountFilled) and OR of
        // WasRemovedFromBook across all snapshots for that maker. Rolling back to the earliest
        // snapshot's state, with the cumulative filled delta, restores the maker as if it had
        // never been touched in this batch.
        var byMaker = new Dictionary<int, MakerRollback>();
        var fillToTaker = new Dictionary<Transaction, Order>(ReferenceEqualityComparer.Instance);

        for (int m = 0; m < matches.Count; m++)
        {
            var (taker, match) = matches[m];
            for (int s = 0; s < match.MakerSnapshots.Count; s++)
            {
                var snap = match.MakerSnapshots[s];
                var id = snap.Order.OrderId;
                if (byMaker.TryGetValue(id, out var agg))
                {
                    if (snap.OriginalAmountFilled < agg.EarliestSnap.OriginalAmountFilled)
                        agg.EarliestSnap = snap;
                    agg.AnyRemovedFromBook |= snap.WasRemovedFromBook;
                    byMaker[id] = agg;
                }
                else
                {
                    byMaker[id] = new MakerRollback
                    {
                        EarliestSnap = snap,
                        AnyRemovedFromBook = snap.WasRemovedFromBook,
                    };
                }
            }
            for (int f = 0; f < match.Fills.Count; f++)
                fillToTaker[match.Fills[f]] = taker;
        }

        // Aggregate rejected qty into the same per-maker entry. Per-taker rejected qty
        // stays in its own dictionary because it's keyed by Order (the taker), not by id.
        var rejectedQtyByTaker = new Dictionary<Order, int>();
        for (int i = 0; i < rejected.Count; i++)
        {
            var rj = rejected[i];
            if (byMaker.TryGetValue(rj.MakerOrderId, out var agg))
            {
                agg.RejectedQty += rj.Trade.Quantity;
                agg.Reason = rj.Reason;
                byMaker[rj.MakerOrderId] = agg;
            }
            // No maker entry means we'll log + skip below; nothing to update here.

            if (fillToTaker.TryGetValue(rj.Trade, out var taker))
            {
                rejectedQtyByTaker.TryGetValue(taker, out var tqty);
                rejectedQtyByTaker[taker] = tqty + rj.Trade.Quantity;
            }
            else
            {
                _logger.LogError(
                    "Rejected fill (maker #{MakerId}, qty {Qty}) not found in any MatchResult.Fills",
                    rj.MakerOrderId, rj.Trade.Quantity);
            }
        }

        // Roll back each maker exactly once with the cumulative delta.
        foreach (var kv in byMaker)
        {
            var makerId = kv.Key;
            var agg = kv.Value;
            if (agg.RejectedQty == 0) continue; // Maker had snapshots but no rejected fills.

            var maker = agg.EarliestSnap.Order;
            // Forward-compat guard: only limit orders sit in the book today (UpsertOrder
            // is gated on IsOpenLimitOrder), so a maker reopened via Status = Open must
            // be a limit order. If a future change ever inserts non-limits we want the
            // assertion to fire here, not produce a silently mis-restored book.
            if (!maker.IsLimitOrder)
                throw new InvalidOperationException(
                    $"Rollback assumes limit-only makers, got {maker.OrderType} for #{makerId}.");

            // RollbackMakerFill handles both "still in book" (credit level qty) and
            // "was removed" (re-insert + credit) cases. We then remove the maker since
            // it's being cancelled.
            var filledDelta = maker.AmountFilled - agg.EarliestSnap.OriginalAmountFilled;
            maker.AmountFilled = agg.EarliestSnap.OriginalAmountFilled;
            maker.Status = Order.Statuses.Open;
            book.RollbackMakerFill(maker, filledDelta, agg.AnyRemovedFromBook);

            maker.Cancel();
            book.RemoveById(makerId);
            ordersById[makerId] = maker;

            // Section 5a: release the maker's Position.ReservedQuantity. Without this
            // every rollback leaves the cancelled order's reservation parked on the
            // seller's Position — AccountsCache's view of AvailableQuantity shrinks
            // permanently while the bot's view (which derives committed from open
            // orders only) keeps reporting full availability, so every subsequent
            // sell from that user/stock fails Phase 1.5 with InsufficientShares.
            // Buyer-side makers never reach this path (validate-pass is seller-only),
            // so there's no symmetric fund-reservation release here. The Order's
            // Cancelled status is persisted by the caller via UpdateAllAsync;
            // Position writes back to DB the next time a fill touches it.
            //
            // Clamp the release to the live ReservedQuantity rather than try/catch
            // ArgumentException — under 20k bots the first-chance exception window
            // floods the debugger even when the catch is intentional. A peer path
            // (CancelOrdersBatchAsync, CancelRemainderAsync) may have already
            // released some or all of this maker's reservation, so the actual
            // release amount is min(remaining, live reserved).
            if (maker.IsSellOrder && maker.RemainingQuantity > 0)
            {
                var pos = _accounts.GetPosition(maker.UserId, maker.StockId);
                if (pos is not null)
                {
                    var toRelease = Math.Min(maker.RemainingQuantity, pos.ReservedQuantity);
                    if (toRelease > 0)
                    {
                        pos.UnreserveStock(toRelease);
                        pos.UpdatedAt = TimeHelper.NowUtc();
                    }
                }
            }

            if (!DebugUserId.HasValue || maker.UserId == DebugUserId.Value)
                _logger.LogWarning(
                    "Cancelled stale maker order #{OrderId} (seller {UserId}, stock {StockId}): {Reason}",
                    makerId, maker.UserId, maker.StockId, agg.Reason);
        }

        // Reduce each taker's AmountFilled and reopen if matcher had flipped it to Filled.
        foreach (var kv in rejectedQtyByTaker)
        {
            var taker = kv.Key;
            taker.AmountFilled -= kv.Value;
            if (taker.Status == Order.Statuses.Filled) taker.Status = Order.Statuses.Open;
        }
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
            // Same forward-compat guard as RollbackRejectedFills — book holds limits only.
            if (!snap.Order.IsLimitOrder)
                throw new InvalidOperationException(
                    $"Rollback assumes limit-only makers, got {snap.Order.OrderType} for #{snap.Order.OrderId}.");

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
