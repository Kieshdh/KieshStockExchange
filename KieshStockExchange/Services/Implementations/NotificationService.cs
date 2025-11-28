using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

public sealed class NotificationService : INotificationService
{
    #region Fields and constructor
    private readonly ILogger<NotificationService> _logger;
    private readonly IStockService _stock;
    private readonly SemaphoreSlim _notifyGate = new(1, 1);

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
            await PushNotificationAsync("Order", "Unknown result.", ct).ConfigureAwait(false);
            return;
        }

        // Build notification
        var (title, message) = await BuildFromOrderResultAsync(result, ct).ConfigureAwait(false);

        // Show notification
        await PushNotificationAsync(title, message, ct).ConfigureAwait(false);
    }

    public async Task NotifyFillAsync(Order orderAfterFill, Transaction fill, CancellationToken ct = default)
    {
        if (orderAfterFill is null || fill is null)
        {
            await PushNotificationAsync("Order Update", "Invalid fill data.", ct).ConfigureAwait(false);
            return;
        }

        // Build notification
        var (title, message) = await BuildFromFillAsync(orderAfterFill, fill, ct).ConfigureAwait(false);

        // Show notification
        await PushNotificationAsync(title, message, ct).ConfigureAwait(false);
    }

    public async Task PushNotificationAsync(string title, string message, CancellationToken ct = default)
    {
        // Acquire the gate unless canceled
        try { await _notifyGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Notification canceled before showing: {Title}", title);
            return; // nothing acquired, nothing to release
        }

        try
        {
            title = string.IsNullOrWhiteSpace(title) ? "Notice" : title.Trim();
            message = string.IsNullOrWhiteSpace(message) ? "—" : message.Trim();

            _logger.LogInformation("Push: {Title} - {Message}", title, message);

            // Always show on UI thread. If no page yet, just log.
            if (Application.Current?.MainPage is null)
            {
                _logger.LogWarning("No MainPage; skipping notification: {Title}", title);
                return;
            }

            if (MainThread.IsMainThread)
                await Application.Current.MainPage.DisplayAlert(title, message, "OK");
            else
                await MainThread.InvokeOnMainThreadAsync(() =>
                    Application.Current!.MainPage!.DisplayAlert(title, message, "OK")
                );
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to show notification."); }
        finally { _notifyGate.Release(); }
    }
    #endregion

    #region Private helpers
    private async Task<(string title, string message)> BuildFromOrderResultAsync(OrderResult r, CancellationToken ct)
    {
        var o = r.PlacedOrder;
        var symbol = o is null ? "Order" : await TryGetSymbolAsync(o.StockId, ct).ConfigureAwait(false);

        // Failure path
        if (!r.PlacedSuccessfully)
        {
            // Failure/edge reasons
            var reason = string.IsNullOrWhiteSpace(r.ErrorMessage) ? r.Status.ToString() : r.ErrorMessage;
            return ($"{symbol}: {r.Status}", reason);
        }

        // Should not happen, but just in case
        if (o is null)
            return ($"{symbol}: Order update", "Order details unavailable.");

        // Success paths (placed / partial / filled / on-book)
        var type = o.OrderType;
        var qty = o.Quantity;
        var avg = CurrencyHelper.Format(r.AverageFillPrice, o.CurrencyType);
        var rem = r.RemainingQuantity;

        switch (r.Status)
        {
            case OrderStatus.Filled: // Fully filled now
                return ($"{symbol}: Order #{o.OrderId} filled",
                    $"{type} {qty} {symbol} @ {avg} (avg)\n" +
                    $"Order #{o.OrderId} completed.");

            case OrderStatus.PartialFill: // Some filled right now
                return ($"{symbol}: Order #{o.OrderId} partially filled",
                    $"{type} {r.TotalFilledQuantity}/{qty} {symbol} @ {avg} (avg) - Remaining: {rem}.");

            case OrderStatus.PlacedOnBook: // Resting limit, nothing filled yet
                return ($"{symbol}: Order #{o.OrderId} placed on book",
                    $"{type} {qty} {symbol} @ {o.PriceDisplay}\n" +
                    $"Order #{o.OrderId} is resting. We’ll notify you on fills.");

            default: // ‘Success’ status from market path
                return ($"{symbol}: Order update", r.SuccessMessage);
        }
    }

    private async Task<(string title, string message)> BuildFromFillAsync(Order o, Transaction tx, CancellationToken ct)
    {
        // tx.Price is the maker’s price; show exact fill and the new remaining.
        var symbol = await TryGetSymbolAsync(o.StockId, ct).ConfigureAwait(false);
        var side = o.IsBuyOrder ? "Buy" : "Sell";

        return o.IsOpen
            ? ($"{symbol}: Partial fill", // Still open
                $"{side} {tx.Quantity} {symbol} @ {tx.PriceDisplay}\n" +
                $"Filled: {o.AmountFilled}/{o.Quantity} • Remaining: {o.RemainingQuantity}.")
            : ($"{symbol}: Order filled", // Closed = fully filled
                $"{side} {tx.Quantity} {symbol} @ {tx.PriceDisplay}\n" +
                $"Order #{o.OrderId} is now complete.");
    }

    private async Task<string> TryGetSymbolAsync(int stockId, CancellationToken ct)
    {
        try
        {
            if (stockId <= 0) return "Stock";

            // First try from in-memory snapshot
            await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_stock.TryGetById(stockId, out var stock))
                 return stock!.Symbol;

            // Try reloading once more, since it might be a new stock
            await _stock.RefreshAsync(ct).ConfigureAwait(false);
            if (_stock.TryGetById(stockId, out stock))
                 return stock!.Symbol;

            // Fallback
            return $"Stock #{stockId}";
        }
        catch { return $"Stock #{stockId}"; }
    }
    #endregion
}
