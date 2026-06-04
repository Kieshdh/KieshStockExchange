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

    // §3.6 P3: a stop's primary price field is its trigger (StopPrice); a stop-limit also exposes
    // a separate limit-price field. PriceFieldLabel relabels the first field accordingly.
    [ObservableProperty] private bool _isStopOrder;
    [ObservableProperty] private bool _isStopLimit;
    [ObservableProperty] private string _priceFieldLabel = "Limit price";
    [ObservableProperty] private string _limitPriceString = string.Empty;

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
        IsStopOrder = false;
        IsStopLimit = false;
        PriceFieldLabel = "Limit price";
        PriceString = string.Empty;
        LimitPriceString = string.Empty;
        QuantityString = string.Empty;
        ErrorMessage = string.Empty;
    }

    private void Initialize(Order order, decimal? prefillPrice)
    {
        TargetOrder = order;

        Summary = $"#{order.OrderId}  {order.SideDisplay} {order.Quantity} @ {order.PriceDisplay}";
        SideTypeChip = $"{order.SideDisplay} · {order.TypeDisplay}";
        IsBuyOrder = order.IsBuyOrder;
        IsStopOrder = order.IsStopOrder;
        IsStopLimit = order.IsStopLimitOrder;
        PriceFieldLabel = order.IsStopOrder ? "Trigger price" : "Limit price";

        // The primary (draggable) field is the trigger for a stop, the limit price otherwise.
        // The chart line for a stop is drawn at StopPrice, so a drag-derived prefill is the
        // new trigger; both paths therefore seed PriceString consistently.
        var basePrice = order.IsStopOrder ? (order.StopPrice ?? order.Price) : order.Price;
        var seedPrice = prefillPrice is decimal p && p > 0m ? p : basePrice;
        PriceString    = CurrencyHelper.FormatForEdit(seedPrice, order.CurrencyType);
        // A stop-limit's separate limit price (not draggable); blank for everything else.
        LimitPriceString = order.IsStopLimitOrder
            ? CurrencyHelper.FormatForEdit(order.Price, order.CurrencyType)
            : string.Empty;
        QuantityString = order.Quantity.ToString();
        ErrorMessage   = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy || TargetOrder is null) return;
        ErrorMessage = string.Empty;

        var (newPrice, newLimitPrice, newQty, validationError) = ValidateInputs(TargetOrder);
        if (validationError is not null) { ErrorMessage = validationError; return; }
        if (newPrice is null && newLimitPrice is null && newQty is null)
        {
            // No change — leave edit mode silently rather than confirming a no-op.
            _editService.EndEdit();
            return;
        }

        IsBusy = true;
        try
        {
            // §3.6 P3: for an armed stop the primary field is the trigger and there may be a
            // separate limit price; route to ModifyStopAsync. Plain orders go to ModifyOrderAsync.
            var result = TargetOrder.IsStopOrder
                ? await _orders.ModifyStopAsync(_auth.CurrentUserId,
                    TargetOrder.OrderId, newQty, newStopPrice: newPrice, newLimitPrice: newLimitPrice).ConfigureAwait(false)
                : await _orders.ModifyOrderAsync(_auth.CurrentUserId,
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

    // Returns the changed values (null = unchanged). For a stop, NewPrice is the new trigger and
    // NewLimitPrice the new stop-limit price; for a plain order, NewPrice is the new limit price
    // and NewLimitPrice is always null.
    private (decimal? NewPrice, decimal? NewLimitPrice, int? NewQty, string? Error) ValidateInputs(Order order)
    {
        decimal? newPrice = null;
        decimal? newLimitPrice = null;
        int? newQty = null;

        // The primary field compares against the trigger for a stop, the limit price otherwise.
        var primaryBase = order.IsStopOrder ? (order.StopPrice ?? 0m) : order.Price;
        var trimmed = (PriceString ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var parsed = CurrencyHelper.Parse(trimmed, order.CurrencyType);
            if (!parsed.HasValue || parsed.Value <= 0m)
                return (null, null, null, order.IsStopOrder ? "Enter a valid positive stop price." : "Enter a valid positive price.");

            if (parsed.Value != primaryBase)
                newPrice = parsed.Value;
        }

        // Stop-limit's separate limit-price field.
        if (order.IsStopLimitOrder)
        {
            var limTrimmed = (LimitPriceString ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(limTrimmed))
            {
                var parsedLim = CurrencyHelper.Parse(limTrimmed, order.CurrencyType);
                if (!parsedLim.HasValue || parsedLim.Value <= 0m)
                    return (null, null, null, "Enter a valid positive limit price.");
                if (parsedLim.Value != order.Price)
                    newLimitPrice = parsedLim.Value;
            }
        }

        if (!int.TryParse(QuantityString, out var q) || q <= 0)
            return (null, null, null, "Enter a valid positive quantity.");

        if (q < order.AmountFilled)
            return (null, null, null, $"Quantity must be ≥ filled amount ({order.AmountFilled}).");

        if (q != order.Quantity) newQty = q;

        return (newPrice, newLimitPrice, newQty, null);
    }

    public void Dispose() => _editService.PropertyChanged -= OnEditServiceChanged;
}
