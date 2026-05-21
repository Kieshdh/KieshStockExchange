using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

/// <summary>
/// Backs the inline Modify Order panel that swaps in for PlaceOrderView when
/// the user enters edit mode (chart drag or ✎ button). Reads the target order
/// from <see cref="IOrderEditService"/>; price + quantity are editable, side
/// and type are locked. Confirm calls OrderEntryService.ModifyOrderAsync and
/// then leaves edit mode; Cancel just leaves edit mode.
/// </summary>
public partial class ModifyOrderViewModel : BaseViewModel, IDisposable
{
    private readonly IOrderEntryService _orders;
    private readonly IOrderCacheService _cache;
    private readonly IUserPortfolioService _portfolio;
    private readonly IAuthService _auth;
    private readonly IOrderEditService _editService;
    private readonly INotificationService _notify;
    private readonly ILogger<ModifyOrderViewModel> _logger;

    public ModifyOrderViewModel(IOrderEntryService orders, IOrderCacheService cache,
        IUserPortfolioService portfolio,
        IAuthService auth, IOrderEditService editService,
        INotificationService notify,
        ILogger<ModifyOrderViewModel> logger)
    {
        Title = "Modify order";
        _orders      = orders      ?? throw new ArgumentNullException(nameof(orders));
        _cache       = cache       ?? throw new ArgumentNullException(nameof(cache));
        _portfolio   = portfolio   ?? throw new ArgumentNullException(nameof(portfolio));
        _auth        = auth        ?? throw new ArgumentNullException(nameof(auth));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _notify      = notify      ?? throw new ArgumentNullException(nameof(notify));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));

        _editService.PropertyChanged += OnEditServiceChanged;
    }

    [ObservableProperty] private Order? _targetOrder;

    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _sideTypeChip = string.Empty;
    [ObservableProperty] private bool _isBuyOrder;

    [ObservableProperty] private string _priceString = string.Empty;
    [ObservableProperty] private string _quantityString = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // When the user types a new price into the modify panel, push it back into
    // IOrderEditService.PrefillPrice. The chart view subscribes to that service
    // and moves the dragged line to track the panel's value live. Round-trips
    // are loop-safe because UpdatePrefillPrice no-ops when the value matches
    // and ObservableProperty no-ops when PriceString already equals the new
    // formatted value.
    partial void OnPriceStringChanged(string value)
    {
        var order = _editService.EditingOrder;
        if (order is null) return;
        var parsed = CurrencyHelper.Parse((value ?? string.Empty).Trim(), order.CurrencyType);
        if (parsed.HasValue && parsed.Value > 0m)
            _editService.UpdatePrefillPrice(parsed.Value);
    }

    private void OnEditServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Marshal to the UI thread because triggers can come from platform input
        // threads (chart-drag pointer release).
        if (e.PropertyName == nameof(IOrderEditService.EditingOrder))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var order = _editService.EditingOrder;
                if (order is null) Reset();
                else Initialize(order, _editService.PrefillPrice);
            });
        }
        else if (e.PropertyName == nameof(IOrderEditService.PrefillPrice))
        {
            // The user re-dragged the chart line while the modify panel was
            // already open. Refresh the price field to the new dragged value.
            // The order itself hasn't changed, so leave qty + summary alone.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var order = _editService.EditingOrder;
                if (order is null) return;
                if (_editService.PrefillPrice is decimal p && p > 0m)
                    PriceString = CurrencyHelper.FormatForEdit(p, order.CurrencyType);
            });
        }
    }

    private void Reset()
    {
        TargetOrder = null;
        Summary = string.Empty;
        SideTypeChip = string.Empty;
        IsBuyOrder = false;
        PriceString = string.Empty;
        QuantityString = string.Empty;
        ErrorMessage = string.Empty;
    }

    private void Initialize(Order order, decimal? prefillPrice)
    {
        TargetOrder = order;

        Summary = $"#{order.OrderId}  {order.SideDisplay} {order.Quantity} @ {order.PriceDisplay}";
        SideTypeChip = $"{order.SideDisplay} · {order.TypeDisplay}";
        IsBuyOrder = order.IsBuyOrder;

        var seedPrice = prefillPrice is decimal p && p > 0m ? p : order.Price;
        PriceString    = CurrencyHelper.FormatForEdit(seedPrice, order.CurrencyType);
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
            // No change — leave edit mode silently rather than confirming a no-op.
            _editService.EndEdit();
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _orders.ModifyOrderAsync(_auth.CurrentUserId,
                TargetOrder.OrderId, newQty, newPrice).ConfigureAwait(false);

            _logger.LogInformation("Modify order #{OrderId}: {Status}",
                TargetOrder.OrderId, result.Status);

            await _notify.NotifyOrderResultAsync(result).ConfigureAwait(false);

            await _cache.RefreshAsync(_auth.CurrentUserId).ConfigureAwait(false);
            // Re-pull funds + positions so the AccountPage Funds card and any
            // portfolio-driven UI see the new reservation. Without this the
            // engine's in-tx fund persist is real but the UI never repaints.
            await _portfolio.RefreshAsync(null).ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() => _editService.EndEdit());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modify order failed for #{OrderId}", TargetOrder.OrderId);
            ErrorMessage = ex.Message;
            try
            {
                await _notify.PushNotificationAsync("Modify failed", ex.Message,
                    NotificationSeverity.Error).ConfigureAwait(false);
            }
            catch (Exception inner) { _logger.LogError(inner, "Modify-failure notification push threw."); }
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _editService.EndEdit();

    private (decimal? NewPrice, int? NewQty, string? Error) ValidateInputs(Order order)
    {
        decimal? newPrice = null;
        int? newQty = null;

        var trimmed = (PriceString ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var parsed = CurrencyHelper.Parse(trimmed, order.CurrencyType);
            if (!parsed.HasValue || parsed.Value <= 0m)
                return (null, null, "Enter a valid positive price.");

            if (parsed.Value != order.Price)
                newPrice = parsed.Value;
        }

        if (!int.TryParse(QuantityString, out var q) || q <= 0)
            return (null, null, "Enter a valid positive quantity.");

        if (q < order.AmountFilled)
            return (null, null, $"Quantity must be ≥ filled amount ({order.AmountFilled}).");

        if (q != order.Quantity) newQty = q;

        return (newPrice, newQty, null);
    }

    public void Dispose() => _editService.PropertyChanged -= OnEditServiceChanged;
}
