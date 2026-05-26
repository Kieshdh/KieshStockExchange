using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketEngineServices.Interfaces;

/// <summary>
/// Client-only UI feed for the order book. ViewModels bind here instead of
/// the engine's mutable <c>OrderBook</c>. Snapshots arrive via SignalR push
/// (throttled to ~10 Hz on the server); the initial fetch on stock-select
/// goes through HTTP.
/// </summary>
public interface IOrderBookFeed
{
    /// <summary>HTTP-fetch the latest snapshot for the key, write to cache, raise <see cref="SnapshotChanged"/>. Null on transport failure.</summary>
    Task<OrderBookSnapshot?> GetSnapshotAsync(int stockId, CurrencyType currency, CancellationToken ct = default);

    /// <summary>Most-recently-observed snapshot for the key. Null until the first HTTP or SignalR delivery for that key.</summary>
    OrderBookSnapshot? TryGetCached(int stockId, CurrencyType currency);

    /// <summary>Fires for every cache update — HTTP or SignalR — whose <c>BookVersion</c> is strictly newer than the previously cached one.</summary>
    event EventHandler<OrderBookSnapshot>? SnapshotChanged;
}
