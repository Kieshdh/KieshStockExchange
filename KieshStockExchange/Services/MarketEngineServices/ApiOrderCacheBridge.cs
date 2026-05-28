using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// Phase 3 finish — bridges the SignalR "OrderUpdated" push (from
/// server-side <see cref="SignalROrderCacheService"/>) onto the client's
/// <see cref="IOrderCacheService.RefreshAsync"/> and
/// <see cref="ITransactionService.RefreshAsync"/>. Eagerly resolved at app
/// boot so the subscription is always live; no ViewModel has to wire it up.
/// </summary>
public sealed class ApiOrderCacheBridge : IDisposable
{
    private readonly IMarketHubClient _hub;
    private readonly IOrderCacheService _cache;
    private readonly ITransactionService _transactions;
    private readonly IAuthService _auth;
    private readonly ILogger<ApiOrderCacheBridge> _logger;

    public ApiOrderCacheBridge(IMarketHubClient hub, IOrderCacheService cache,
        ITransactionService transactions, IAuthService auth,
        ILogger<ApiOrderCacheBridge> logger)
    {
        _hub = hub;
        _cache = cache;
        _transactions = transactions;
        _auth = auth;
        _logger = logger;
        _hub.OrderUpdated += OnHubOrderUpdated;
    }

    private void OnHubOrderUpdated(object? sender, int pushedUserId)
    {
        // Only refresh if the push is for the currently-active user. The
        // server pushes per user even when the client is logged in as
        // someone else (e.g. admin impersonation) — ignore those.
        var active = _auth.CurrentUserId;
        if (active <= 0 || pushedUserId != active) return;

        _ = RefreshSafelyAsync(active);
    }

    // Order pushes correlate 1:1 with fills, so a fresh transaction pull
    // here is the cheapest way to keep history views in sync without a
    // dedicated TransactionsUpdated push channel.
    private async Task RefreshSafelyAsync(int userId)
    {
        try { await _cache.RefreshAsync(userId).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "OrderCache refresh failed for user {UserId}", userId); }

        try { await _transactions.RefreshAsync(userId).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "TransactionService refresh failed for user {UserId}", userId); }
    }

    public void Dispose() => _hub.OrderUpdated -= OnHubOrderUpdated;
}
