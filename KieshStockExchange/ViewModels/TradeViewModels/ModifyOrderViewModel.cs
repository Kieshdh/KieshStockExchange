using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
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
    private readonly IAuthService _auth;
    private readonly IOrderEditService _editService;
    private readonly ILogger<ModifyOrderViewModel> _logger;

    public ModifyOrderViewModel(IOrderEntryService orders, IOrderCacheService cache,
        IAuthService auth, IOrderEditService editService,
        ILogger<ModifyOrderViewModel> logger)
    {
        Title = "Modify order";
        _orders      = orders      ?? throw new ArgumentNullException(nameof(orders));
        _cache       = cache       ?? throw new ArgumentNullException(nameof(cache));
        _auth        = auth        ?? throw new ArgumentNullException(nameof(auth));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
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

    private void OnEditServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IOrderEditService.EditingOrder)) return;

        // Service flipping back to null clears the form; flipping to a new order
        // initialises it. Marshal to the UI thread because the trigger could be
        // a chart-drag pointer release on the platform input thread.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var order = _editService.EditingOrder;
            if (order is null) Reset();
            else Initialize(order, _editService.PrefillPrice);
        });
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

            await _cache.RefreshAsync(_auth.CurrentUserId).ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() => _editService.EndEdit());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modify order failed for #{OrderId}", TargetOrder.OrderId);
            ErrorMessage = ex.Message;
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
