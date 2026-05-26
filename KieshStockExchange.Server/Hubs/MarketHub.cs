using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace KieshStockExchange.Server.Hubs;

// One SignalR hub per server, three group families. Clients subscribe per visible
// stock/currency (quotes), per logged-in user (orders, portfolio). Server-side
// publishing happens via MarketHubBroadcaster in Step 6 — this file just owns the
// subscribe/unsubscribe surface.
//
// JoinCandles / LeaveCandles also start/stop the per-resolution aggregator on
// the server's CandleService. Without that the engine's flush loop has no
// aggregator for the requested key and CandleClosed never fires (the chart's
// "live" indicator never updates and the candle stream looks frozen).
//
// Auth is deferred to Phase 5. Until then JoinUserGroups trusts the userId the
// client supplies; after JWT lands the hub will derive it from a claim instead.
public sealed class MarketHub : Hub
{
    private readonly ICandleService _candles;
    public MarketHub(ICandleService candles) => _candles = candles;

    public Task JoinQuotes(int stockId, CurrencyType currency) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupNameQuotes(stockId, currency));

    public Task LeaveQuotes(int stockId, CurrencyType currency) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNameQuotes(stockId, currency));

    public Task JoinCandles(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        // Bumps the server-side aggregator's ref count for this key. The CandleClosed
        // event will fire once the next bucket boundary elapses with at least one
        // trade in the book.
        _candles.Subscribe(stockId, currency, resolution);
        // Candle pushes piggyback the quotes group — both fire on the same key.
        // Calling AddToGroupAsync here is a no-op if the client already joined
        // quotes (which the chart path always does first).
        return Groups.AddToGroupAsync(Context.ConnectionId, GroupNameQuotes(stockId, currency));
    }

    public Task LeaveCandles(int stockId, CurrencyType currency, CandleResolution resolution) =>
        _candles.UnsubscribeAsync(stockId, currency, resolution, Context.ConnectionAborted);

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
