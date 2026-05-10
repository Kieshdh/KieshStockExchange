using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
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
    private readonly ILogger<AiBotStateService> _logger;

    // Throttle "Applied active bot cap" — the scaler may toggle the cap several
    // times per minute when load wobbles. One INFO per change buries everything
    // else; collapse identical state and rate-limit the rest to ApplyCapLogInterval.
    private static readonly TimeSpan ApplyCapLogInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastApplyCapLogAt = DateTime.MinValue;
    private int? _lastLoggedCap;
    private int _lastLoggedEnabled = -1;
    private int _suppressedApplyCapCount;

    internal AiBotStateService(IDataBaseService db, ILogger<AiBotStateService> logger)
    {
        _db     = db     ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        ctx.Positions.Clear();
        ctx.Funds.Clear();
        ctx.OpenOrders.Clear();

        var userIds = ctx.AiUsersByAiUserId.Values
            .Where(u => u.IsEnabled).Select(u => u.UserId).ToList();
        if (userIds.Count == 0) return;

        var allFunds     = await _db.GetFundsForUsersAsync(userIds, ct).ConfigureAwait(false);
        var allPositions = await _db.GetPositionsForUsersAsync(userIds, ct).ConfigureAwait(false);
        var allOrders    = await _db.GetOpenOrdersForUsersAsync(userIds, ct).ConfigureAwait(false);

        foreach (var g in allFunds.GroupBy(f => f.UserId))
            ctx.Funds[g.Key] = g.ToDictionary(f => f.CurrencyType, f => f);

        foreach (var g in allPositions.GroupBy(p => p.UserId))
            ctx.Positions[g.Key] = g.ToDictionary(p => p.StockId, p => p);

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

        // Skip the log when nothing changed, otherwise rate-limit so rapid scaler
        // bursts collapse into one summary line.
        bool stateChanged = !Nullable.Equals(_lastLoggedCap, cap) || _lastLoggedEnabled != enabled;
        if (!stateChanged) return;

        var now = TimeHelper.NowUtc();
        bool firstEver = _lastApplyCapLogAt == DateTime.MinValue;
        bool windowElapsed = (now - _lastApplyCapLogAt) >= ApplyCapLogInterval;

        if (firstEver || windowElapsed)
        {
            if (_suppressedApplyCapCount > 0)
                _logger.LogInformation(
                    "Applied active bot cap (cap={Cap}, enabled={Enabled}; +{Suppressed} suppressed change(s) in last {Secs:F0}s)",
                    cap?.ToString() ?? "none", enabled, _suppressedApplyCapCount,
                    (now - _lastApplyCapLogAt).TotalSeconds);
            else
                _logger.LogInformation("Applied active bot cap (cap={Cap}, enabled={Enabled})",
                    cap?.ToString() ?? "none", enabled);

            _lastApplyCapLogAt = now;
            _lastLoggedCap = cap;
            _lastLoggedEnabled = enabled;
            _suppressedApplyCapCount = 0;
        }
        else
        {
            _suppressedApplyCapCount++;
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
        foreach (var fill in result.FillTransactions)
        {
            RecordTx(ctx, fill);

            var notional = CurrencyHelper.RoundMoney(fill.TotalAmount, fill.CurrencyType);

            // Buyer pays cash, receives shares
            if (ctx.Funds.TryGetValue(fill.BuyerId, out var buyerFunds)
                && buyerFunds.TryGetValue(fill.CurrencyType, out var bf))
                bf.TotalBalance -= notional;

            ctx.GetPosition(fill.BuyerId, fill.StockId).Quantity += fill.Quantity;

            // Seller receives cash, loses shares
            if (ctx.Funds.TryGetValue(fill.SellerId, out var sellerFunds)
                && sellerFunds.TryGetValue(fill.CurrencyType, out var sf))
                sf.TotalBalance += notional;

            if (ctx.Positions.TryGetValue(fill.SellerId, out var sellerPos)
                && sellerPos.TryGetValue(fill.StockId, out var sp))
                sp.Quantity -= fill.Quantity;
        }

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
}
