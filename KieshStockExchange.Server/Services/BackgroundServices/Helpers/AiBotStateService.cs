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

    // Throttle "Applied active bot cap" — the scaler may toggle the cap several
    // times per minute when load wobbles. One INFO per change buries everything
    // else; collapse identical state and rate-limit the rest to ApplyCapLogInterval.
    private static readonly TimeSpan ApplyCapLogInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastApplyCapLogAt = DateTime.MinValue;
    private int? _lastLoggedCap;
    private int _lastLoggedEnabled = -1;
    private int _suppressedApplyCapCount;

    // Prune knobs: how stale before forced cancel, what fraction of MaxOpenOrders
    // triggers capacity culling, how far off market a limit must be to qualify,
    // and how many capacity-victims to cancel per bot per pass.
    private static readonly TimeSpan PruneStaleAge = TimeSpan.FromMinutes(3);
    private const decimal PruneDistanceFactor = 2.0m;
    private const int PruneOrdersPerBot = 2;

    internal AiBotStateService(IDataBaseService db, IAccountsCache accounts,
        IOrderExecutionService orders, BotStatsLogger stats,
        ILogger<AiBotStateService> logger)
    {
        _db       = db       ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _orders   = orders   ?? throw new ArgumentNullException(nameof(orders));
        _stats    = stats    ?? throw new ArgumentNullException(nameof(stats));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Load and Refresh
    internal async Task LoadAsync(AiBotContext ctx, CancellationToken ct)
    {
        ctx.AiUsersByAiUserId.Clear();
        ctx.AiUsersByUserId.Clear();
        ctx.AiUserRngs.Clear();

        foreach (var user in await _db.GetAIUsersAsync(ct).ConfigureAwait(false))
        {
            ctx.AiUsersByAiUserId[user.AiUserId] = user;
            ctx.AiUsersByUserId[user.UserId]     = user;
            ctx.GetRandom(user.AiUserId); // seed RNG eagerly
        }

        await RefreshAssetsAsync(ctx, ct).ConfigureAwait(false);
    }

    internal async Task RefreshAssetsAsync(AiBotContext ctx, CancellationToken ct)
    {
        ctx.CurrenciesByUser.Clear();
        ctx.StocksByUser.Clear();
        ctx.OpenOrders.Clear();

        var userIds = ctx.AiUsersByAiUserId.Values
            .Where(u => u.IsEnabled).Select(u => u.UserId).ToList();
        if (userIds.Count == 0) return;

        // Make sure the canonical Fund/Position state is loaded in AccountsCache.
        // After this returns, ctx.GetFund / GetPosition will see the live engine view.
        await _accounts.EnsureLoadedAsync(userIds, ct).ConfigureAwait(false);

        // Build the metadata indexes (which currencies / stocks each user touches).
        // We discard the Fund/Position instances themselves — AccountsCache owns those.
        var allFunds     = await _db.GetFundsForUsersAsync(userIds, ct).ConfigureAwait(false);
        var allPositions = await _db.GetPositionsForUsersAsync(userIds, ct).ConfigureAwait(false);
        var allOrders    = await _db.GetOpenOrdersForUsersAsync(userIds, ct).ConfigureAwait(false);

        foreach (var f in allFunds)
        {
            if (!ctx.CurrenciesByUser.TryGetValue(f.UserId, out var set))
                ctx.CurrenciesByUser[f.UserId] = set = new HashSet<CurrencyType>();
            set.Add(f.CurrencyType);
        }

        foreach (var p in allPositions)
        {
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
    /// Cancels stale (older than <see cref="PruneStaleAge"/>) and worst-priced
    /// open limit orders for bots over ~80% of their <c>MaxOpenOrders</c>. Issues
    /// one batched <c>CancelOrdersBatchAsync</c> call regardless of victim count.
    /// </summary>
    /// <param name="sessionStart">Anchor for the stale-age check; orders from before
    /// the current loop session get a fresh grace window so a session restart
    /// doesn't wipe everything on first prune.</param>
    internal async Task PruneWorstOrdersAsync(AiBotContext ctx, DateTime? sessionStart, CancellationToken ct)
    {
        var toCancel = new List<(int userId, Order order)>();

        foreach (var user in ctx.AiUsersByAiUserId.Values)
        {
            if (!ctx.OpenOrders.TryGetValue(user.UserId, out var userOrders) || userOrders.Count == 0)
                continue;

            var limitOrders = userOrders.Values.Where(o => o.IsOpenLimitOrder).ToList();
            if (limitOrders.Count == 0) continue;

            // Criterion 1: stale age — cancel regardless of capacity.
            var anchorTime = sessionStart ?? DateTime.MinValue;
            foreach (var o in limitOrders)
            {
                var effectiveCreated = o.CreatedAt > anchorTime ? o.CreatedAt : anchorTime;
                if (TimeHelper.NowUtc() - effectiveCreated >= PruneStaleAge)
                    toCancel.Add((user.UserId, o));
            }

            // Criterion 2: capacity — only when at ≥80% of MaxOpenOrders.
            if (userOrders.Count < (int)Math.Ceiling(user.MaxOpenOrders * 0.8)) continue;

            var alreadyQueued     = new HashSet<int>(toCancel.Select(x => x.order.OrderId));
            var distanceThreshold = PruneDistanceFactor * user.MaxLimitOffsetPrc;

            var scored = new List<(Order order, decimal distance)>();
            foreach (var o in limitOrders)
            {
                if (alreadyQueued.Contains(o.OrderId)) continue;
                if (!ctx.StockPrices.TryGetValue((o.StockId, o.CurrencyType), out var m) || m <= 0m) continue;
                var dist = o.IsBuyOrder ? (m - o.Price) / m : (o.Price - m) / m;
                if (dist > distanceThreshold) scored.Add((o, dist));
            }

            foreach (var (o, _) in scored.OrderByDescending(x => x.distance).Take(PruneOrdersPerBot))
                toCancel.Add((user.UserId, o));
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "PruneWorstOrders: CancelOrdersBatchAsync failed for {Count} orders", ids.Count);
            return;
        }

        int pruned = 0;
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
                _logger.LogWarning("PruneWorstOrders: cancel of {OrderId} returned {Status}",
                    orderId, result.Status);
            }
        }

        if (pruned > 0) _stats.AddCancelled(pruned);
    }
    #endregion
}
