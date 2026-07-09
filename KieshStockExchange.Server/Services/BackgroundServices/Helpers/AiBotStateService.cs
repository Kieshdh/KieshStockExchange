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
        bool pruneLimitOnly = false, bool leanReload = false)
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
        // Realism §: age cutoff for resting-limit expiry. Computed once per sweep.
        var nowUtc = TimeHelper.NowUtc();
        bool ageExpiry = _orderMaxAgeSec > 0;
        // §stop-ttl: standalone armed stops have no TTL and pile up unbounded; expire the oldest-reached
        // (capped this sweep) via the safe per-order path below. Interim — see the field comment.
        // §B2: PruneLimitOnly SUPERSEDES stop-ttl — the limit-only index carries no armed stops, so the
        // stop-ttl branch could never match anyway; disable it explicitly and warn once so the interim is
        // clearly retired (set Bots:StopMaxAgeSec=0). replace-old (Bots:StopReplaceOld) cures the source.
        bool stopAgeExpiry = _stopMaxAgeSec > 0 && !_pruneLimitOnly;
        if (_pruneLimitOnly && _stopMaxAgeSec > 0 && !_warnedStopTtlSuperseded)
        {
            _warnedStopTtlSuperseded = true;
            _logger.LogInformation("§B2: Bots:PruneLimitOnly is on — the StopMaxAgeSec stop-ttl cull is " +
                "superseded (replace-old bounds the pool) and will not run; set Bots:StopMaxAgeSec=0.");
        }
        var stopVictims = stopAgeExpiry ? new List<(int userId, Order order)>() : null;

        foreach (var user in ctx.AiUsersByAiUserId.Values)
        {
            // §B2: iterate the limit-only index when on (O(limits), no armed stops); else the full set.
            var source = _pruneLimitOnly ? ctx.OpenLimitOrders : ctx.OpenOrders;
            if (!source.TryGetValue(user.UserId, out var userOrders) || userOrders.Count == 0)
                continue;

            // Rule 0 (realism, flag-gated): expire resting limit orders past their per-order randomized
            // lifetime so the book churns instead of accumulating forever; §stop-ttl: same for standalone
            // armed stops. ONE pass over the bot's orders (double-iterating the ~1M book would be the very
            // cost we're cutting). Runs even for bots with no Far band (FarLimitMax<=0 continues below).
            if (ageExpiry || stopAgeExpiry)
            {
                foreach (var o in userOrders.Values)
                {
                    // Jitter the lifetime per order in [0.5x, 1.5x] via an OrderId hash, times a per-bot
                    // patience factor in [0.7x, 1.3x] via a UserId hash (patient vs impatient traders).
                    // Both deterministic, RNG-free, independent and mean-1.0 ⇒ the population mean lifetime
                    // is preserved; the product just disperses expiries (smoother churn).
                    if (ageExpiry && o.IsOpenLimitOrder)
                    {
                        var lifetime = _orderMaxAgeSec * AgeJitterFactor(o.OrderId) * BotLifetimeFactor(user.UserId);
                        if ((nowUtc - o.CreatedAt).TotalSeconds >= lifetime)
                            toCancel.Add((user.UserId, o));
                    }
                    // §stop-ttl: standalone armed stops only — bracket children are EXCLUDED (their reservation
                    // is pooled by the Σ child-reservations == position.ReservedQuantity bracket invariant).
                    // Capped per sweep so the backlog drains gently instead of one mass-cancel spike.
                    else if (stopAgeExpiry && o.IsArmed && !o.IsBracketChild
                             && stopVictims!.Count < _stopCullMaxPerSweep)
                    {
                        var lifetime = _stopMaxAgeSec * AgeJitterFactor(o.OrderId) * BotLifetimeFactor(user.UserId);
                        if ((nowUtc - o.CreatedAt).TotalSeconds >= lifetime)
                            stopVictims!.Add((user.UserId, o));
                    }
                }
            }

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

        // §stop-ttl: cancel aged standalone stops via their own SAFE per-order path, independent of whether
        // any resting limits were culled this sweep.
        if (stopVictims is { Count: > 0 }) await CancelAgedStopsAsync(ctx, stopVictims, ct).ConfigureAwait(false);

        if (toCancel.Count == 0) return;

        var ids = new List<int>(toCancel.Count);
        var seen = new HashSet<int>(toCancel.Count);   // dedup: Rule 0 (age) and Rule 1/2 can both pick an order
        for (int i = 0; i < toCancel.Count; i++)
        {
            var (userId, order) = toCancel[i];
            if (!ctx.OpenOrders.TryGetValue(userId, out var userOrders)) continue;
            if (!userOrders.ContainsKey(order.OrderId)) continue;
            if (!seen.Add(order.OrderId)) continue;
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
                    // §B2: these victims are all resting limits ⇒ keep the limit-only index in sync.
                    if (_pruneLimitOnly && ctx.OpenLimitOrders.TryGetValue(toCancel[j].userId, out var limitOrders))
                        limitOrders.Remove(orderId);
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

    // §stop-ttl: cancel aged standalone armed stops one at a time via the SAFE single-order path.
    // CancelOrderAsync resolves the canonical + CancelRemainderAsync releases the arm reservation (the BATCH
    // path can't — it treats Pending as AlreadyClosed and would leak the reservation + leave the watcher
    // armed). The StopTriggerWatcher promote is defensive on !IsArmed (PromoteStopAsync early-returns), so a
    // lingering index entry cannot phantom-fill. Victims are capped upstream so this drains gently.
    private async Task CancelAgedStopsAsync(AiBotContext ctx, List<(int userId, Order order)> stopVictims,
        CancellationToken ct)
    {
        int pruned = 0, failed = 0;
        foreach (var (userId, order) in stopVictims)
        {
            OrderResult res;
            try
            {
                res = await _orders.CancelOrderAsync(order.OrderId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                if (failed++ == 0) _logger.LogError(ex, "CancelAgedStops: cancel failed for #{Id}", order.OrderId);
                continue;
            }
            if (res.Status == OrderStatus.Success || res.Status == OrderStatus.AlreadyClosed)
            {
                if (ctx.OpenOrders.TryGetValue(userId, out var uo)) uo.Remove(order.OrderId);
                pruned++;
            }
            else if (failed++ == 0)
            {
                _logger.LogWarning("CancelAgedStops: #{Id} not cancelled ({Status})", order.OrderId, res.Status);
            }
        }
        if (pruned > 0)
        {
            _stats.AddCancelled(pruned);
            _logger.LogInformation("§stop-ttl: expired {Pruned} aged standalone armed stops this sweep", pruned);
        }
    }

    // §replace-old (Bots:StopReplaceOld): before a bot ARMS a new standalone protective stop, cancel its
    // existing standalone armed stop(s) on the SAME (stock, side) — so a bot MOVES its stop instead of
    // STACKING a new one on every StopProb/TrailingProb draw (the additive firehose = ~58/bot, ~570/min).
    // Uses the SAFE per-order path (mirrors CancelAgedStopsAsync): CancelOrderAsync resolves the canonical +
    // CancelRemainderAsync releases the arm reservation; the BATCH path can't (treats Pending as AlreadyClosed
    // ⇒ leaks the reservation + leaves the watcher armed = phantom-fill = CK break). Bracket/OCO children
    // EXCLUDED. Called from AiTradeService.SubmitAdvancedAsync BEFORE the new arm. Not a decision-path change
    // (RNG untouched) ⇒ market realism byte-unaffected; it only removes dormant duplicate stops.
    internal async Task CancelPriorStandaloneStopsAsync(AiBotContext ctx, int userId, int stockId,
        OrderSide side, CancellationToken ct)
    {
        // Source the victim order-ids. §B3 lean reload: armed stops aren't hydrated into ctx.OpenOrders, so
        // fetch them with a targeted (bot,stock,side) STANDALONE query. Off: scan the in-memory set (the
        // pre-B3 path, byte-identical). NOTE (ordering caveat, both paths): armed stops enter ctx.OpenOrders /
        // the query reflects DB state only up to the last commit, so this sees the bot's ACCUMULATED prior
        // stops (the hours-long pile), not one armed within the current window — exactly what needs pruning.
        Dictionary<int, Order>? userOrders = null;
        List<int>? victimIds = null;
        if (_leanReload)
        {
            var ids = await _maint.GetStandaloneArmedStopIdsAsync(userId, stockId, side, ct).ConfigureAwait(false);
            if (ids.Count > 0) victimIds = ids;
        }
        else
        {
            if (!ctx.OpenOrders.TryGetValue(userId, out userOrders) || userOrders.Count == 0) return;
            foreach (var o in userOrders.Values)
                if (o.IsArmed && !o.IsBracketChild && o.StockId == stockId && o.Side == side)
                    (victimIds ??= new List<int>()).Add(o.OrderId);
        }
        if (victimIds is null) return;

        int pruned = 0, failed = 0;
        foreach (var orderId in victimIds)
        {
            OrderResult res;
            try
            {
                res = await _orders.CancelOrderAsync(orderId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                if (failed++ == 0) _logger.LogError(ex, "ReplaceOld: cancel failed for #{Id}", orderId);
                continue;
            }
            if (res.Status == OrderStatus.Success || res.Status == OrderStatus.AlreadyClosed)
            {
                if (_leanReload)
                {
                    // Keep the cap exact intra-window — mirrors the off-path OpenOrders.Remove decrementing
                    // .Count. Clamp: the query set can exceed the stale reload count (arms since last reload
                    // aren't in it), so an unclamped decrement would underflow.
                    if (ctx.ArmedStopCount.TryGetValue(userId, out var cnt))
                        ctx.ArmedStopCount[userId] = Math.Max(0, cnt - 1);
                }
                else
                {
                    // Armed stop — never in the limit-only index, so no OpenLimitOrders touch needed.
                    userOrders!.Remove(orderId);
                }
                pruned++;
            }
            else if (failed++ == 0)
            {
                _logger.LogWarning("ReplaceOld: #{Id} not cancelled ({Status})", orderId, res.Status);
            }
        }
        if (pruned > 0) _stats.AddCancelled(pruned);
    }

    // Realism §: per-order lifetime jitter in [0.5, 1.5] from an OrderId avalanche hash. Deterministic
    // and RNG-free (no draw, call-order-independent) so flag-on stays reproducible; spreads expiries so
    // the book churns smoothly instead of mass-cancelling a whole cohort on one sweep.
    private static double AgeJitterFactor(int orderId)
    {
        unchecked
        {
            ulong h = (ulong)orderId * 0x9E3779B97F4A7C15UL + 0x165667B19E3779F9UL;
            h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
            double u = (h & 0xFFFFFFFFUL) / (double)0xFFFFFFFFUL;   // [0,1]
            return 0.5 + u;                                         // [0.5, 1.5]
        }
    }

    // Realism §: per-bot patience factor in [0.7, 1.3] from a UserId avalanche hash (distinct mixing
    // constants from AgeJitterFactor so the two factors are uncorrelated). Mean 1.0 ⇒ keeps the base mean
    // lifetime unchanged; gives each bot its own holding horizon (patient vs impatient). RNG-free.
    private static double BotLifetimeFactor(int userId)
    {
        unchecked
        {
            ulong h = (ulong)userId * 0xD1B54A32D192ED03UL + 0x2545F4914F6CDD1DUL;
            h ^= h >> 33; h *= 0xC2B2AE3D27D4EB4FUL; h ^= h >> 29;
            double u = (h & 0xFFFFFFFFUL) / (double)0xFFFFFFFFUL;   // [0,1]
            return 0.7 + 0.6 * u;                                   // [0.7, 1.3]
        }
    }
    #endregion
}
