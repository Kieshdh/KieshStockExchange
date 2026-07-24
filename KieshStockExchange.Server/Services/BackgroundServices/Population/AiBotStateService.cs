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
internal sealed partial class AiBotStateService
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

    // Realism §: resting-limit-order lifetime in seconds. 0 = disabled (default, byte-identical).
    // When > 0, PruneWorstOrdersAsync cancels open limit orders past a per-order randomized age so
    // the book churns instead of accumulating resting orders without bound (the deep-book that damps
    // steady-state volatility over a long soak). Per-order jitter via an OrderId hash avoids
    // synchronized mass-cancels that would print an artificial volatility spike.
    private readonly int _orderMaxAgeSec;
    // §stop-ttl (INTERIM — ultraplan docs/ultraplan-prompt-maint-tick-scaling.md; CHANGE BACK when B2 lands):
    // armed stops have NO expiry (retention leaves them), so they accumulate unbounded and bloat the O(book)
    // prune scan (the maint-phase blowup). >0 gives standalone armed stops a per-order jittered TTL, cancelled
    // via the SAFE single-order path (CancelOrderAsync releases the arm reservation; the batch path can't). The
    // watcher's promote is defensive on !IsArmed so a lingering index entry cannot phantom-fill. Cull is capped
    // per sweep (_stopCullMaxPerSweep) so the backlog drains gently instead of one mass-cancel spike. 0 = off.
    private readonly int _stopMaxAgeSec;
    private readonly int _stopCullMaxPerSweep;
    // §B2 (Bots:PruneLimitOnly): when on, maintain ctx.OpenLimitOrders (a limit-only mirror) and make
    // PruneWorstOrdersAsync iterate it instead of the full ctx.OpenOrders — so the ~30s prune is O(limits),
    // independent of the armed-stop count (the maint-growth term). Off ⇒ prune reads OpenOrders as before,
    // no mirror maintained ⇒ byte-identical. Supersedes the §stop-ttl interim (StopMaxAgeSec).
    private readonly bool _pruneLimitOnly;
    private bool _warnedStopTtlSuperseded;
    // §B3 (Bots:LeanReload): when on, RefreshAssetsAsync fetches only open LIMITS into ctx.OpenOrders (not the
    // ~1.18M armed stops) + a per-bot armed-stop COUNT into ctx.ArmedStopCount, so the ~60s reload is O(limits)
    // instead of O(pool). The cap adds ArmedStopCount; replace-old sources its victims from a DB query instead
    // of scanning ctx.OpenOrders. Off ⇒ full hydration as before ⇒ byte-identical. Server-only query surface.
    private readonly IBotMaintenanceQueries _maint;
    private readonly bool _leanReload;
    // §source-cap (Bots:MaxArmedStopsPerBot): per-bot armed-stop CAP. 0 = off. When > 0 (and LeanReload on),
    // NoteArmedStopPlaced increments ctx.ArmedStopCount on each standalone protective-stop arm so the count is
    // exact intra-window (replace-old already decrements it), letting BuildProtectiveStopAsync reject a bot's
    // new arm past the cap. Bounds the pool at the SOURCE (the disease cure). Off ⇒ no increment ⇒ byte-identical.
    private readonly int _maxArmedStopsPerBot;

    // Throttle "Applied active bot cap" — the scaler may toggle the cap several
    // times per minute when load wobbles. One INFO per change buries everything
    // else; collapse identical state and rate-limit the rest to ApplyCapLogInterval.
    private static readonly TimeSpan ApplyCapLogInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastApplyCapLogAt = DateTime.MinValue;
    private int? _lastLoggedCap;
    private int _lastLoggedEnabled = -1;

    internal AiBotStateService(IDataBaseService db, IAccountsCache accounts,
        IOrderExecutionService orders, BotStatsLogger stats,
        ILogger<AiBotStateService> logger, IBotMaintenanceQueries maint, decimal distanceMult = 1m,
        int orderMaxAgeSec = 0, int stopMaxAgeSec = 0, int stopCullMaxPerSweep = 500,
        bool pruneLimitOnly = false, bool leanReload = false, int maxArmedStopsPerBot = 0)
    {
        _db       = db       ?? throw new ArgumentNullException(nameof(db));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _orders   = orders   ?? throw new ArgumentNullException(nameof(orders));
        _stats    = stats    ?? throw new ArgumentNullException(nameof(stats));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _maint    = maint    ?? throw new ArgumentNullException(nameof(maint));
        _distanceMult = distanceMult <= 0m ? 1m : distanceMult;
        _orderMaxAgeSec = orderMaxAgeSec < 0 ? 0 : orderMaxAgeSec;
        _stopMaxAgeSec = stopMaxAgeSec < 0 ? 0 : stopMaxAgeSec;
        _stopCullMaxPerSweep = stopCullMaxPerSweep < 1 ? 1 : stopCullMaxPerSweep;
        _pruneLimitOnly = pruneLimitOnly;
        _leanReload = leanReload;
        _maxArmedStopsPerBot = maxArmedStopsPerBot < 0 ? 0 : maxArmedStopsPerBot;
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
        if (_pruneLimitOnly) ctx.OpenLimitOrders.Clear();   // §B2: rebuilt in lock-step below
        if (_leanReload) ctx.ArmedStopCount.Clear();        // §B3: rebuilt from the GROUP-BY below

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
        // §B3 lean reload: fetch only open LIMITS (~96k) instead of limits + ~1.18M armed stops. The armed-stop
        // info the decision path still needs — the per-bot cap COUNT — comes from a cheap GROUP-BY below;
        // replace-old sources its victims from a targeted query. Off ⇒ the full fetch (byte-identical). The
        // shared GetOpenOrdersForUsersAsync is intentionally NOT narrowed (AccountsCache reseed depends on it).
        var allOrders = _leanReload
            ? await _maint.GetOpenLimitOrdersForUsersAsync(userIds, ct).ConfigureAwait(false)
            : await _db.GetOpenOrdersForUsersAsync(userIds, ct).ConfigureAwait(false);
        if (_leanReload)
        {
            var counts = await _maint.GetArmedStopCountsByUserAsync(userIds, ct).ConfigureAwait(false);
            foreach (var kv in counts) ctx.ArmedStopCount[kv.Key] = kv.Value;
        }

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
        {
            ctx.OpenOrders[g.Key] = g.ToDictionary(o => o.OrderId, o => o);
            // §B2: mirror only the resting limits into the prune's limit-only index (same GroupBy pass).
            if (_pruneLimitOnly)
            {
                Dictionary<int, Order>? lim = null;
                foreach (var o in g)
                    if (o.IsOpenLimitOrder) (lim ??= new Dictionary<int, Order>())[o.OrderId] = o;
                if (lim != null) ctx.OpenLimitOrders[g.Key] = lim;
            }
        }
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
            // §mm-cohort: the house market-maker cohort is a small fixed liquidity floor — always on, and not
            // counted against the cap so it never crowds out the random fleet's budget (mirrors arbitrage).
            if (user.Strategy == AiStrategy.MarketMakerHouse) { user.IsEnabled = true; continue; }
            // §rotator: the estimate-driven rotational cohort is a fixed house set — always on and cap-exempt.
            // Its per-tick load is throttled at decision time by Bots:Rotator:ParticipationFraction, not the cap.
            if (user.Strategy == AiStrategy.Rotator) { user.IsEnabled = true; continue; }
            // §conviction: the discretionary conviction cohort is a fixed set — always on and cap-exempt. Its
            // per-tick load is throttled at decision time by the occasional-cadence fire gate, not the cap.
            if (user.Strategy == AiStrategy.Conviction) { user.IsEnabled = true; continue; }

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
            // §B2: keep the limit-only index in lock-step (this add-site is already limit-gated).
            if (_pruneLimitOnly)
            {
                if (!ctx.OpenLimitOrders.ContainsKey(placed.UserId))
                    ctx.OpenLimitOrders[placed.UserId] = new Dictionary<int, Order>();
                ctx.OpenLimitOrders[placed.UserId][placed.OrderId] = placed;
            }
        }
    }
    #endregion
}
