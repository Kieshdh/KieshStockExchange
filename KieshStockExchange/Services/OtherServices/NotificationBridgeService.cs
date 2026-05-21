using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.OtherServices;

/// <summary>
/// Bridges OrderCacheService.OrdersChanged into INotificationService toasts.
/// Diffs AmountFilled per order across refreshes so the user gets a partial /
/// final fill notification when a bot taker hits one of their resting orders.
/// </summary>
public sealed class NotificationBridgeService : IDisposable
{
    private readonly IOrderCacheService _cache;
    private readonly INotificationService _notify;
    private readonly IStockService _stocks;
    private readonly ILogger<NotificationBridgeService> _logger;

    private readonly Dictionary<int, int> _lastFilledByOrderId = new();
    private bool _baselineSeeded;
    private bool _disposed;

    public NotificationBridgeService(IOrderCacheService cache, INotificationService notify,
        IStockService stocks, ILogger<NotificationBridgeService> logger)
    {
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _cache.OrdersChanged += OnOrdersChanged;
    }

    private void OnOrdersChanged(object? sender, EventArgs e)
    {
        try
        {
            var current = _cache.AllOrders;

            // First refresh after login — record baseline and stay silent so we
            // don't fire a fill notification for every historical order.
            if (!_baselineSeeded)
            {
                foreach (var o in current) _lastFilledByOrderId[o.OrderId] = o.AmountFilled;
                _baselineSeeded = true;
                return;
            }

            foreach (var o in current)
            {
                if (!_lastFilledByOrderId.TryGetValue(o.OrderId, out var prevFilled))
                {
                    // New order to us — record and skip; PlaceOrderViewModel already
                    // raised the placement notification.
                    _lastFilledByOrderId[o.OrderId] = o.AmountFilled;
                    continue;
                }

                if (o.AmountFilled > prevFilled)
                {
                    var delta = o.AmountFilled - prevFilled;
                    _ = NotifyFillDeltaAsync(o, delta);
                }
                _lastFilledByOrderId[o.OrderId] = o.AmountFilled;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Notification bridge OnOrdersChanged threw."); }
    }

    private async Task NotifyFillDeltaAsync(Order o, int delta)
    {
        try
        {
            var symbol = _stocks.TryGetById(o.StockId, out var stock) ? stock!.Symbol : $"Stock #{o.StockId}";
            var side = o.IsBuyOrder ? "Buy" : "Sell";

            var title = o.IsOpen ? $"{symbol}: Partial fill" : $"{symbol}: Order filled";
            var message = o.IsOpen
                ? $"{side} {delta} {symbol} · Filled {o.AmountFilled}/{o.Quantity} · Remaining: {o.RemainingQuantity}."
                : $"{side} {delta} {symbol} · Order #{o.OrderId} complete.";
            var severity = o.IsOpen ? NotificationSeverity.Info : NotificationSeverity.Success;

            await _notify.PushNotificationAsync(title, message, severity).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogError(ex, "Fill notification failed for order {OrderId}.", o.OrderId); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.OrdersChanged -= OnOrdersChanged;
    }
}
