using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

/// <summary>
/// Backs the Modify Order popup. Opened from OpenOrdersView (Trade page) or
/// PortfolioOpenOrdersView with a target Order; lets the user change Price and
/// Quantity, then routes through OrderEntryService.ModifyOrderAsync. Mirrors
/// the Binance Spot edit dialog: two fields pre-filled with current values,
/// Confirm + Cancel.
/// </summary>
public partial class ModifyOrderViewModel : BaseViewModel
{
    private readonly IOrderEntryService _orders;
    private readonly IOrderCacheService _cache;
    private readonly IAuthService _auth;
    private readonly ILogger<ModifyOrderViewModel> _logger;

    /// <summary>Raised after Confirm succeeds or Cancel is pressed so the host
    /// page can close the window.</summary>
    public event EventHandler? CloseRequested;

    public ModifyOrderViewModel(IOrderEntryService orders, IOrderCacheService cache,
        IAuthService auth, ILogger<ModifyOrderViewModel> logger)
    {
        Title = "Modify order";
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [ObservableProperty] private Order? _targetOrder;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _priceLabel = "Price";
    [ObservableProperty] private bool _canEditPrice = true;

    [ObservableProperty] private string _priceString = string.Empty;
    [ObservableProperty] private string _quantityString = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Initialise the form for the given order. Prefills the price + quantity
    /// fields with the existing values so the user only edits what changes.
    /// </summary>
    public void Initialize(Order order, decimal? prefillPrice = null)
    {
        TargetOrder = order ?? throw new ArgumentNullException(nameof(order));

        Summary = $"#{order.OrderId}  {order.SideDisplay} {order.Quantity} @ {order.PriceDisplay}";

        CanEditPrice = !order.IsMarketOrder;
        PriceLabel = order.IsMarketOrder ? "Price (market — not editable)" : "Limit price";

        var seedPrice = !order.IsMarketOrder && prefillPrice is decimal p && p > 0m
            ? p
            : order.Price;

        PriceString    = order.IsMarketOrder ? "—" : seedPrice.ToString("0.######");
        QuantityString = order.Quantity.ToString();
        ErrorMessage   = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy || TargetOrder is null) return;
        ErrorMessage = string.Empty;

        var (newPrice, newQty, validationError) = ValidateInputs(TargetOrder);
        if (validationError is not null) { ErrorMessage = validationError; return; }
        if (newPrice is null && newQty is null)
        {
            // No change — close silently rather than confirming a no-op.
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _orders.ModifyOrderAsync(_auth.CurrentUserId,
                TargetOrder.OrderId, newQty, newPrice).ConfigureAwait(false);

            _logger.LogInformation("Modify order #{OrderId}: {Status}",
                TargetOrder.OrderId, result.Status);

            // Refresh the cache so the open-orders list and chart price lines
            // reflect the modified order before the window closes.
            await _cache.RefreshAsync(_auth.CurrentUserId).ConfigureAwait(false);

            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modify order failed for #{OrderId}", TargetOrder.OrderId);
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private (decimal? NewPrice, int? NewQty, string? Error) ValidateInputs(Order order)
    {
        decimal? newPrice = null;
        int? newQty = null;

        if (CanEditPrice)
        {
            var trimmed = (PriceString ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                var parsed = CurrencyHelper.Parse(trimmed, order.CurrencyType);
                if (!parsed.HasValue || parsed.Value <= 0m)
                    return (null, null, "Enter a valid positive price.");

                if (parsed.Value != order.Price)
                    newPrice = parsed.Value;
            }
        }

        if (!int.TryParse(QuantityString, out var q) || q <= 0)
            return (null, null, "Enter a valid positive quantity.");

        if (q < order.AmountFilled)
            return (null, null, $"Quantity must be ≥ filled amount ({order.AmountFilled}).");

        if (q != order.Quantity) newQty = q;

        return (newPrice, newQty, null);
    }
}
