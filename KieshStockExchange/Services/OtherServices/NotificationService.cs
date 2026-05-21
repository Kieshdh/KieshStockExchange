using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace KieshStockExchange.Services.OtherServices;

public sealed class NotificationService : INotificationService
{
    #region Fields and constructor
    private const int MaxBuffered = 50;

    private readonly ILogger<NotificationService> _logger;
    private readonly IStockService _stock;

    // Newest first. ConcurrentQueue would reverse-order; a lock + LinkedList
    // keeps Recent ordering trivial and bounded.
    private readonly LinkedList<Notification> _ring = new();
    private readonly object _ringLock = new();

    public event EventHandler<Notification>? NotificationAdded;

    public IReadOnlyList<Notification> Recent
    {
        get
        {
            lock (_ringLock) return _ring.ToArray();
        }
    }

    public NotificationService(ILogger<NotificationService> logger, IStockService stock)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));
    }
    #endregion

    #region INotificationService Implementation
    public async Task NotifyOrderResultAsync(OrderResult result, CancellationToken ct = default)
    {
        if (result is null)
        {
            await PushNotificationAsync("Order", "Unknown result.", NotificationSeverity.Warning, ct).ConfigureAwait(false);
            return;
        }

        var (title, message, severity) = await BuildFromOrderResultAsync(result, ct).ConfigureAwait(false);
        await PushNotificationAsync(title, message, severity, ct).ConfigureAwait(false);
    }

    public async Task NotifyFillAsync(Order orderAfterFill, Transaction fill, CancellationToken ct = default)
    {
        if (orderAfterFill is null || fill is null)
        {
            await PushNotificationAsync("Order Update", "Invalid fill data.", NotificationSeverity.Warning, ct).ConfigureAwait(false);
            return;
        }

        var (title, message, severity) = await BuildFromFillAsync(orderAfterFill, fill, ct).ConfigureAwait(false);
        await PushNotificationAsync(title, message, severity, ct).ConfigureAwait(false);
    }

    public Task PushNotificationAsync(string title, string message,
        NotificationSeverity severity = NotificationSeverity.Info,
        CancellationToken ct = default)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Notice" : title.Trim();
        message = string.IsNullOrWhiteSpace(message) ? "—" : message.Trim();

        var note = new Notification
        {
            Title = title,
            Message = message,
            Severity = severity
        };

        Notification? evicted = null;
        lock (_ringLock)
        {
            _ring.AddFirst(note);
            if (_ring.Count > MaxBuffered)
            {
                evicted = _ring.Last!.Value;
                _ring.RemoveLast();
            }
        }

        _logger.LogInformation("Notify [{Severity}]: {Title} - {Message}", severity, title, message);
        if (evicted is not null)
            _logger.LogDebug("Notification ring evicted oldest: {EvictedTitle}", evicted.Title);

        // Raise on the UI thread so subscribers (ToastHost, TopNavBar inbox)
        // can mutate ObservableCollections without dispatcher gymnastics.
        var handler = NotificationAdded;
        if (handler is not null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { handler.Invoke(this, note); }
                catch (Exception ex) { _logger.LogError(ex, "NotificationAdded handler threw."); }
            });
        }

        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_ringLock) _ring.Clear();
    }
    #endregion

    #region Private helpers
    private async Task<(string Title, string Message, NotificationSeverity Severity)>
        BuildFromOrderResultAsync(OrderResult r, CancellationToken ct)
    {
        var o = r.PlacedOrder;
        var symbol = o is null ? "Order" : await TryGetSymbolAsync(o.StockId, ct).ConfigureAwait(false);

        if (!r.PlacedSuccessfully)
        {
            var reason = string.IsNullOrWhiteSpace(r.ErrorMessage) ? r.Status.ToString() : r.ErrorMessage;
            return ($"{symbol}: {r.Status}", reason, NotificationSeverity.Error);
        }

        if (o is null)
            return ($"{symbol}: Order update", "Order details unavailable.", NotificationSeverity.Info);

        var type = o.OrderType;
        var qty = o.Quantity;
        var avg = CurrencyHelper.Format(r.AverageFillPrice, o.CurrencyType);
        var rem = r.RemainingQuantity;

        switch (r.Status)
        {
            case OrderStatus.Filled:
                return ($"{symbol}: Order #{o.OrderId} filled",
                    $"{type} {qty} {symbol} @ {avg} (avg)\nOrder #{o.OrderId} completed.",
                    NotificationSeverity.Success);

            case OrderStatus.PartialFill:
                return ($"{symbol}: Order #{o.OrderId} partially filled",
                    $"{type} {r.TotalFilledQuantity}/{qty} {symbol} @ {avg} (avg) - Remaining: {rem}.",
                    NotificationSeverity.Info);

            case OrderStatus.PlacedOnBook:
                return ($"{symbol}: Order #{o.OrderId} placed on book",
                    $"{type} {qty} {symbol} @ {o.PriceDisplay}\nOrder #{o.OrderId} is resting. We'll notify you on fills.",
                    NotificationSeverity.Info);

            default:
                return ($"{symbol}: Order update", r.SuccessMessage, NotificationSeverity.Info);
        }
    }

    private async Task<(string Title, string Message, NotificationSeverity Severity)>
        BuildFromFillAsync(Order o, Transaction tx, CancellationToken ct)
    {
        var symbol = await TryGetSymbolAsync(o.StockId, ct).ConfigureAwait(false);
        var side = o.IsBuyOrder ? "Buy" : "Sell";

        return o.IsOpen
            ? ($"{symbol}: Partial fill",
                $"{side} {tx.Quantity} {symbol} @ {tx.PriceDisplay}\nFilled: {o.AmountFilled}/{o.Quantity} • Remaining: {o.RemainingQuantity}.",
                NotificationSeverity.Info)
            : ($"{symbol}: Order filled",
                $"{side} {tx.Quantity} {symbol} @ {tx.PriceDisplay}\nOrder #{o.OrderId} is now complete.",
                NotificationSeverity.Success);
    }

    private async Task<string> TryGetSymbolAsync(int stockId, CancellationToken ct)
    {
        try
        {
            if (stockId <= 0) return "Stock";

            await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_stock.TryGetById(stockId, out var stock))
                return stock!.Symbol;

            await _stock.RefreshAsync(ct).ConfigureAwait(false);
            if (_stock.TryGetById(stockId, out stock))
                return stock!.Symbol;

            return $"Stock #{stockId}";
        }
        catch { return $"Stock #{stockId}"; }
    }
    #endregion
}
