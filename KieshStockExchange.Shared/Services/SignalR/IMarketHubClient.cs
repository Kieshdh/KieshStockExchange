using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.PortfolioServices.Interfaces;

namespace KieshStockExchange.Services.SignalR;

/// <summary>
/// Single shared connection to the server's MarketHub (/hubs/market). Every
/// client-side SignalR/HTTP proxy that needs live state subscribes here
/// instead of opening its own connection. Group membership is owned by this
/// client so reconnects can replay it.
/// </summary>
public interface IMarketHubClient
{
    /// <summary>Underlying connection state (Disconnected, Connecting, Connected, Reconnecting).</summary>
    string State { get; }

    /// <summary>Fires whenever <see cref="State"/> transitions.</summary>
    event EventHandler<string>? StateChanged;

    /// <summary>QuoteUpdated push from server-side IMarketDataService.</summary>
    event EventHandler<LiveQuote>? QuoteUpdated;

    /// <summary>CandleClosed push from server-side ICandleService flush loop.</summary>
    event EventHandler<Candle>? CandleClosed;

    /// <summary>OrderUpdated push from server-side SignalROrderCacheService. Payload is the userId whose orders changed.</summary>
    event EventHandler<int>? OrderUpdated;

    /// <summary>PortfolioChanged push from server-side IUserPortfolioService.</summary>
    event EventHandler<PortfolioSnapshot>? PortfolioChanged;

    /// <summary>Connect to /hubs/market if not already. Safe to call multiple times.</summary>
    Task EnsureConnectedAsync(CancellationToken ct = default);

    /// <summary>Close the connection. Group memberships are cleared.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Join quotes:{stockId}:{currency} so QuoteUpdated/CandleClosed pushes arrive for this pair.</summary>
    Task JoinQuotesAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    Task LeaveQuotesAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary>
    /// Start aggregating candles at the given resolution for this book on the
    /// server. Triggers per-bucket CandleClosed pushes onto the quotes:{stockId}:
    /// {currency} group. The default chart resolution is the only one already
    /// running server-side at boot; any other resolution needs this join.
    /// </summary>
    Task JoinCandlesAsync(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct = default);

    Task LeaveCandlesAsync(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct = default);

    /// <summary>Join orders:{userId} and portfolio:{userId} for the active user.</summary>
    Task JoinUserGroupsAsync(int userId, CancellationToken ct = default);

    Task LeaveUserGroupsAsync(int userId, CancellationToken ct = default);
}
