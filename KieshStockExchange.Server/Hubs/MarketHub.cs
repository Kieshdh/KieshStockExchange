using KieshStockExchange.Helpers;
using Microsoft.AspNetCore.SignalR;

namespace KieshStockExchange.Server.Hubs;

// One SignalR hub per server, three group families. Clients subscribe per visible
// stock/currency (quotes), per logged-in user (orders, portfolio). Server-side
// publishing happens via MarketHubBroadcaster in Step 6 — this file just owns the
// subscribe/unsubscribe surface.
//
// Auth is deferred to Phase 5. Until then JoinUserGroups trusts the userId the
// client supplies; after JWT lands the hub will derive it from a claim instead.
public sealed class MarketHub : Hub
{
    public Task JoinQuotes(int stockId, CurrencyType currency) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupNameQuotes(stockId, currency));

    public Task LeaveQuotes(int stockId, CurrencyType currency) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNameQuotes(stockId, currency));

    public async Task JoinUserGroups(int userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupNameOrders(userId));
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupNamePortfolio(userId));
    }

    public async Task LeaveUserGroups(int userId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNameOrders(userId));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNamePortfolio(userId));
    }

    public static string GroupNameQuotes(int stockId, CurrencyType currency) => $"quotes:{stockId}:{currency}";
    public static string GroupNameOrders(int userId) => $"orders:{userId}";
    public static string GroupNamePortfolio(int userId) => $"portfolio:{userId}";
}
