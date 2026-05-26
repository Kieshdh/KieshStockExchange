using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketEngineServices;

/// <summary>
/// HTTP bootstrap + SignalR push, mirroring <c>SignalRMarketDataClient</c>'s
/// cache-and-replay shape. Replaces the in-process IOrderBookCache the chart
/// VM used to read directly via SelectedStockService.CurrentOrderBook.
/// </summary>
public sealed class ApiOrderBookFeed : IOrderBookFeed, IDisposable
{
    #region Services and state
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMarketHubClient _hub;
    private readonly ILogger<ApiOrderBookFeed> _logger;

    private readonly ConcurrentDictionary<(int, CurrencyType), OrderBookSnapshot> _cache = new();

    public event EventHandler<OrderBookSnapshot>? SnapshotChanged;
    #endregion

    #region Constructor and Dispose
    public ApiOrderBookFeed(IHttpClientFactory httpFactory, IMarketHubClient hub,
        ILogger<ApiOrderBookFeed> logger)
    {
        _httpFactory = httpFactory;
        _hub = hub;
        _logger = logger;
        _hub.OrderBookSnapshotReceived += OnHubSnapshot;
    }

    public void Dispose() => _hub.OrderBookSnapshotReceived -= OnHubSnapshot;
    #endregion

    #region Public surface
    public OrderBookSnapshot? TryGetCached(int stockId, CurrencyType currency) =>
        _cache.TryGetValue((stockId, currency), out var s) ? s : null;

    public async Task<OrderBookSnapshot?> GetSnapshotAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        try
        {
            var http = _httpFactory.CreateClient("KSE.Server");
            var snap = await http.GetFromJsonAsync<OrderBookSnapshot>(
                $"api/order-book/{stockId}/{currency}", ApiJsonOptions.Default, ct).ConfigureAwait(false);
            if (snap is not null) ApplyIfNewer(snap);
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /api/order-book/{Stock}/{Currency} failed", stockId, currency);
            return null;
        }
    }
    #endregion

    #region Push + cache update
    private void OnHubSnapshot(object? sender, OrderBookSnapshot snap) => ApplyIfNewer(snap);

    private void ApplyIfNewer(OrderBookSnapshot snap)
    {
        // BookVersion gate — drops out-of-order pushes (rare with SignalR but free to guard).
        var key = (snap.StockId, snap.Currency);
        if (_cache.TryGetValue(key, out var existing) && snap.BookVersion <= existing.BookVersion)
            return;

        _cache[key] = snap;
        try { SnapshotChanged?.Invoke(this, snap); }
        catch (Exception ex) { _logger.LogWarning(ex, "SnapshotChanged subscriber threw for {Stock}/{Currency}", snap.StockId, snap.Currency); }
    }
    #endregion
}
