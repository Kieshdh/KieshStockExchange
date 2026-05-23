using KieshStockExchange.Models;
using KieshStockExchange.Server.Hubs;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace KieshStockExchange.Services.MarketEngineServices;

// Phase 3 Step 6: replaces NoopOrderCacheService. The engine still calls
// _orderCache.NotifyOrdersMutated(userIds) on every successful settle / cancel
// / modify; this impl forwards the notification to each user's
// orders:{userId} SignalR group. Clients subscribe to that group on login and
// kick OrderCacheService.RefreshAsync() when the event arrives.
//
// The list/Refresh surface of IOrderCacheService stays no-op on the server —
// the cache itself is client-side, only the notification surface is honoured.
public sealed class SignalROrderCacheService : IOrderCacheService
{
    private readonly IHubContext<MarketHub> _hub;
    private readonly ILogger<SignalROrderCacheService> _logger;

    public SignalROrderCacheService(IHubContext<MarketHub> hub, ILogger<SignalROrderCacheService> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public List<Order> AllOrders { get; } = new();
    public IReadOnlyList<Order> OpenOrders { get; } = Array.Empty<Order>();
    public IReadOnlyList<Order> ClosedOrders { get; } = Array.Empty<Order>();
    public event EventHandler? OrdersChanged { add { } remove { } }
    public Task<bool> RefreshAsync(int userId, CancellationToken ct = default) => Task.FromResult(false);

    public void NotifyOrdersMutated(IReadOnlyCollection<int> affectedUserIds)
    {
        if (affectedUserIds is null || affectedUserIds.Count == 0) return;
        foreach (var userId in affectedUserIds)
        {
            // Fire-and-forget: the SignalR layer queues per connection and we
            // don't block the engine path on transport. Failures are logged but
            // don't propagate — a missed push just means the next user action
            // triggers a fresh refresh.
            _ = _hub.Clients.Group(MarketHub.GroupNameOrders(userId))
                .SendAsync("OrderUpdated", new { UserId = userId })
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) _logger.LogWarning(t.Exception, "Failed to push OrderUpdated for user {UserId}", userId);
                }, TaskScheduler.Default);
        }
    }
}
