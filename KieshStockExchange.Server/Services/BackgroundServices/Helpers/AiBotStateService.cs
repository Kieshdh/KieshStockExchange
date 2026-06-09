using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Manages bot lifecycle: loading, asset refresh, daily housekeeping,
/// online-status recalculation, transaction recording, and cache updates after fills.
/// </summary>
internal sealed class AiBotStateService
{
    #region Services and Constructor
    private readonly IDataBaseService _db;
    private readonly IAccountsCache _accounts;
    private readonly IOrderExecutionService _orders;
    private readonly BotStatsLogger _stats;
    private readonly ILogger<AiBotStateService> _logger;

    // §P6 tightness dial: must match the decision service so the prune's Far-distance thresholds track
    // where Far orders are actually placed (FarLimit*Prc × this). Otherwise orders placed at the dialed
    // distance drift far past it before the straggler cull fires, leaving the book wider than intended.
    private readonly decimal _distanceMult;

    // Throttle "Applied active bot cap" — the scaler may toggle the cap several
    // times per minute when load wobbles. One INFO per change buries everything
    // else; collapse identical state and rate-limit the rest to ApplyCapLogInterval.
    private static readonly TimeSpan ApplyCapLogInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastApplyCapLogAt = DateTime.MinValue;
    private int? _lastLoggedCap;
    private int _lastLoggedEnabled = -1;

    internal AiBotStateService(IDataBaseService db, IAccountsCache accounts,
        IOrderExecutionService orders, BotStatsLogger stats,
        ILogger<AiBotStateService> logger, decimal distanceMult = 1m)
    {
        _db       = db       ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _orders   = orders   ?? throw new ArgumentNullException(nameof(orders));
        _stats    = stats    ?? throw new ArgumentNullException(nameof(stats));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _distanceMult = distanceMult <= 0m ? 1m : distanceMult;
    }
    #endregion

    #region Load and Refresh
    internal async Task LoadAsync(AiBotContext ctx, CancellationToken ct)
    {
        var users = await _db.GetAIUsersAsync(ct).ConfigureAwait(false);

        // Lock the dict while we wipe + repopulate so admin readers don't
        // observe a half-cleared state. Reader side: AiTradeService.OnlineBotCount.
        lock (ctx.AiUsersByAiUserId)
        {
            ctx.AiUsersByAiUserId.Clear();
            ctx.AiUsersByUserId.Clear();
            ctx.AiUserRngs.Clear();

            foreach (var user in users)
            {
                ctx.AiUsersByAiUserId[user.AiUserId] = user;
                ctx.AiUsersByUserId[user.UserId]     = user;
                ctx.GetRandom(user.AiUserId); // seed RNG eagerly
            }
        }

        await RefreshAssetsAsync(ctx, ct).ConfigureAwait(false);
    }

    internal async Task RefreshAssetsAsync(AiBotContext ctx, CancellationToken ct)
    {
        ctx.StocksByUser.Clear();
        ctx.OpenOrders.Clear();

        var userIds = ctx.AiUsersByAiUserId.Values
            .Where(u => u.IsEnabled).Select(u => u.UserId).ToList();
        if (userIds.Count == 0) return;

        // Make sure the canonical Fund/Position state is loaded in AccountsCache.
        // After this returns, ctx.GetFund / GetPosition will see the live engine view.
        await _accounts.EnsureLoadedAsync(userIds, ct).ConfigureAwait(false);

        // Build the metadata index (which stocks each user actually HOLDS a position in).
        // We discard the Fund/Position instances themselves — AccountsCache owns those.
        // Funds are already warmed into AccountsCache by EnsureLoadedAsync above; the
        // bot loop reads currency state live via ctx.GetFund, so no per-user currency
        // index is built here.
        var allPositions = await _db.GetPositionsForUsersAsync(userIds, ct).ConfigureAwait(false);
        var allOrders    = await _db.GetOpenOrdersForUsersAsync(userIds, ct).ConfigureAwait(false);

        // Only index live (non-zero) positions: the seed gives every bot a row for ALL 50 stocks,
        // so an unfiltered index degenerates to the whole universe per bot. Both readers
        // (PortfolioValueByCurrency, LogSnapshot) already skip Quantity<=0, so filtering here is
        // output-identical but shrinks the per-bot walk from 50 to the ~13.5 actually held.
        foreach (var p in allPositions)
        {
            if (p.Quantity == 0) continue;
            if (!ctx.StocksByUser.TryGetValue(p.UserId, out var set))
                ctx.StocksByUser[p.UserId] = set = new HashSet<int>();
            set.Add(p.StockId);
        }

        foreach (var g in allOrders.GroupBy(o => o.UserId))
            ctx.OpenOrders[g.Key] = g.ToDictionary(o => o.OrderId, o => o);
    }
    #endregion

    #region Daily and Online Management
    internal void CheckDailyRefresh(AiBotContext ctx)
    {
        if (ctx.LastRefreshDate == TimeHelper.Today()) return;

        ctx.LastRefreshDate = TimeHelper.Today();
        ctx.AiUserRngs.Clear();
        ctx.ProcessedTxIds.Clear();

        foreach (var user in ctx.AiUsersByAiUserId.Values)
            user.ResetDay();

        _logger.LogInformation("Performed daily refresh for AI users on {Date}",
            TimeHelper.Today().ToString("yyyy-MM-dd"));
    }

    internal void ApplyActiveBotCap(AiBotContext ctx, int? cap)
    {
        int enabled = 0;
        foreach (var user in ctx.AiUsersByAiUserId.Values)
        {
            // §3.7 the arbitrage cohort always runs regardless of the active-bot cap / scaler —
            // it's a small fixed set and disabling it would break cross-listing parity. It also
            // doesn't count against the cap so it never crowds out the random fleet's budget.
            if (user.Strategy == AiStrategy.Arbitrage) { user.IsEnabled = true; continue; }

            bool active = !cap.HasValue || enabled < cap.Value;
            user.IsEnabled = active;
            if (active) enabled++;
        }

        // The "Applied active bot cap" info log was removed per user feedback —
        // the BotScalerService log already captures the same cap-change event
        // with its load %/EWMA context. Bookkeeping fields stay so callers that
        // read _lastLoggedCap / _lastLoggedEnabled don't break.
        if (!Nullable.Equals(_lastLoggedCap, cap) || _lastLoggedEnabled != enabled)
        {
            _lastLoggedCap = cap;
            _lastLoggedEnabled = enabled;
            _lastApplyCapLogAt = TimeHelper.NowUtc();
        }
    }
    #endregion

    #region Transaction and Cache
    internal void RecordTx(AiBotContext ctx, Transaction tx)
    {
        if (tx == null || tx.IsInvalid) return;
        if (tx.Timestamp < TimeHelper.UtcStartOfToday()) return;
        if (!ctx.ProcessedTxIds.Add(tx.TransactionId)) return;

        if (ctx.AiUsersByUserId.TryGetValue(tx.BuyerId, out var buyer))
        {
            buyer.RecordTrade(tx);
            TriggerBurstOnFill(ctx, buyer.AiUserId, tx.Timestamp);
        }

        if (tx.SellerId == tx.BuyerId) return;

        if (ctx.AiUsersByUserId.TryGetValue(tx.SellerId, out var seller))
        {
            seller.RecordTrade(tx);
            TriggerBurstOnFill(ctx, seller.AiUserId, tx.Timestamp);
        }
    }

    internal void TriggerBurstOnFill(AiBotContext ctx, int aiUserId, DateTime fillTime)
    {
        // 30% chance a fill triggers a short focused trading session
        var alreadyBursting = ctx.BurstEndTimes.TryGetValue(aiUserId, out var end) && fillTime < end;
        if (!alreadyBursting && ctx.Decimal01(aiUserId) < 0.30m)
        {
            var secs = 60 + (int)(ctx.Decimal01(aiUserId) * 180); // 1–4 min
            ctx.BurstEndTimes[aiUserId] = fillTime + TimeSpan.FromSeconds(secs);
        }
    }

    internal void ApplyResultToCache(AiBotContext ctx, OrderResult result)
    {
        // Fund/Position mutations happen in the engine (TradeSettler.SettleNoTxAsync)
        // against the canonical AccountsCache instances — no shadow accounting here.
        // The bot reads live state via ctx.GetFund / ctx.GetPosition on its next decision.
        foreach (var fill in result.FillTransactions)
            RecordTx(ctx, fill);

        // Track newly resting limit orders immediately so CanPlaceMoreOrder and
        // ComputeCommitted* see them before the next RefreshAssetsAsync.
        var placed = result.PlacedOrder;
        if (placed != null && placed.IsOpenLimitOrder)
        {
            if (!ctx.OpenOrders.ContainsKey(placed.UserId))
                ctx.OpenOrders[placed.UserId] = new Dictionary<int, Order>();
            ctx.OpenOrders[placed.UserId][placed.OrderId] = placed;
        }
    }
    #endregion

    #region Pruning
    /// <summary>
    /// §P6 tier-aware prune. Replaces the old stale-age + 2×offset cull (which destroyed the standing
    /// Far-wall ladder). Two rules, classified by each order's <b>current</b> distance from market:
    /// <list type="number">
    /// <item><b>Straggler cull</b> (every sweep, unconditional): cancel any limit order that has drifted
    /// past the bot's <c>FarLimitMaxPrc</c> — dead weight far outside its own band.</item>
    /// <item><b>Far value-budget mass-prune</b> (conditional): an order counts as Far once its current
    /// distance ≥ <c>FarLimitMinPrc</c> (so a Mid that drifts out is included). When the bot's resting
    /// Far value exceeds <c>FarBudgetPrc × portfolio</c> (per currency), cancel worst-first (furthest)
    /// down to ½ the budget (hysteresis).</item>
    /// </list>
    /// Close orders (and in-band Mid) are never pruned — they churn and fill. One batched
    /// <c>CancelOrdersBatchAsync</c> regardless of victim count.
    /// </summary>
    internal async Task PruneWorstOrdersAsync(AiBotContext ctx, CancellationToken ct)
    {
        var toCancel = new List<(int userId, Order order)>();

        foreach (var user in ctx.AiUsersByAiUserId.Values)
        {
            if (!ctx.OpenOrders.TryGetValue(user.UserId, out var userOrders) || userOrders.Count == 0)
                continue;
            // Skip bots that pre-date the tier columns (all-zero) — no Far band to reason about.
            if (user.FarLimitMaxPrc <= 0m) continue;

            // Dial the Far band thresholds to match where the decision service actually places Far orders
            // (FarLimit*Prc × the tightness dial), so the straggler cull fires at the dialed Far edge.
            var farMaxPrc = user.FarLimitMaxPrc * _distanceMult;
            var farMinPrc = user.FarLimitMinPrc * _distanceMult;

            // One pass over the user's orders (no Where/GroupBy allocations — this runs for every bot every
            // 30s sweep, so the per-bot LINQ churn was pure GC pressure): cancel stragglers inline and bucket
            // the Far-budget candidates per currency. Identical victim SET to the old grouped form; the
            // worst-first OrderByDescending below is kept (stable sort → deterministic tie-break).
            Dictionary<CurrencyType, List<(Order order, decimal dist, decimal value)>>? farByCurrency = null;
            foreach (var o in userOrders.Values)
            {
                if (!o.IsOpenLimitOrder) continue;
                if (!ctx.StockPrices.TryGetValue((o.StockId, o.CurrencyType), out var m) || m <= 0m) continue;
                var dist = o.IsBuyOrder ? (m - o.Price) / m : (o.Price - m) / m;

                // Rule 1: straggler — drifted past the bot's own (dialed) Far band.
                if (dist > farMaxPrc) { toCancel.Add((user.UserId, o)); continue; }

                // Otherwise eligible for the Far budget once it sits at/beyond the (dialed) Far band start.
                if (dist >= farMinPrc)
                {
                    farByCurrency ??= new Dictionary<CurrencyType, List<(Order, decimal, decimal)>>();
                    if (!farByCurrency.TryGetValue(o.CurrencyType, out var farList))
                        farByCurrency[o.CurrencyType] = farList = new List<(Order, decimal, decimal)>();
                    farList.Add((o, dist, o.RemainingAmount));
                }
            }

            if (farByCurrency is null) continue;

            // Rule 2 (per currency): Far value-budget mass-prune, worst-first down to ½ budget (hysteresis).
            foreach (var (currency, farList) in farByCurrency)
            {
                var farBudget = user.FarBudgetPrc * ctx.PortfolioValueByCurrency(user.UserId, currency);
                if (farBudget <= 0m) continue;
                decimal farValue = 0m;
                for (int i = 0; i < farList.Count; i++) farValue += farList[i].value;
                if (farValue <= farBudget) continue;
                var target = farBudget / 2m;
                foreach (var (o, _, value) in farList.OrderByDescending(x => x.dist))
                {
                    if (farValue <= target) break;
                    toCancel.Add((user.UserId, o));
                    farValue -= value;
                }
            }
        }

        if (toCancel.Count == 0) return;

        var ids = new List<int>(toCancel.Count);
        for (int i = 0; i < toCancel.Count; i++)
        {
            var (userId, order) = toCancel[i];
            if (!ctx.OpenOrders.TryGetValue(userId, out var userOrders)) continue;
            if (!userOrders.ContainsKey(order.OrderId)) continue;
            ids.Add(order.OrderId);
        }
        if (ids.Count == 0) return;

        IReadOnlyList<OrderResult> results;
        try
        {
            results = await _orders.CancelOrdersBatchAsync(ids, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _logger.LogError(ex, "PruneWorstOrders: CancelOrdersBatchAsync failed for {Count} orders", ids.Count);
            return;
        }

        int pruned = 0;
        int failed = 0;
        int firstFailedId = 0;
        OrderStatus firstFailedStatus = default;
        for (int i = 0; i < results.Count; i++)
        {
            var orderId = ids[i];
            var result = results[i];
            if (result.PlacedSuccessfully || result.Status == OrderStatus.AlreadyClosed)
            {
                for (int j = 0; j < toCancel.Count; j++)
                {
                    if (toCancel[j].order.OrderId != orderId) continue;
                    if (ctx.OpenOrders.TryGetValue(toCancel[j].userId, out var userOrders))
                        userOrders.Remove(orderId);
                    break;
                }
                pruned++;
            }
            else
            {
                // Expected at high bot load: the order is mid-fill/mid-cancel in a
                // concurrent group. Aggregate — one line per pass, not per order.
                if (failed++ == 0) { firstFailedId = orderId; firstFailedStatus = result.Status; }
            }
        }

        if (failed > 0)
            _logger.LogWarning("PruneWorstOrders: {Failed}/{Total} cancels failed (e.g. #{OrderId}: {Status})",
                failed, results.Count, firstFailedId, firstFailedStatus);

        if (pruned > 0) _stats.AddCancelled(pruned);
    }
    #endregion
}
