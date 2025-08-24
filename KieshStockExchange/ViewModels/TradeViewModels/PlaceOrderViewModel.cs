using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.Helpers;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Globalization;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class PlaceOrderViewModel : BaseViewModel
{
    private readonly IMarketOrderService _market;
    private readonly IUserOrderService _orders;
    private readonly ISelectedStockService _selected;
    private readonly ILogger<TradeViewModel> _logger;

    // ---- Tab state: Buy/Sell (0/1) and Limit/Market (0/1) -------------------
    [ObservableProperty] private int sideIndex;       // 0=Buy, 1=Sell
    [ObservableProperty] private int orderTypeIndex;  // 0=Limit, 1=Market

    // Convenience flags for XAML visibility
    [ObservableProperty] private bool _isLimitOrder;
    [ObservableProperty] private bool _isMarketOrder;

    // ---- Inputs --------------------------------------------------------------
    [ObservableProperty] private decimal _limitPrice;         // only for Limit
    [ObservableProperty] private int _quantity;               // shares
    [ObservableProperty] private decimal _slippagePercent = 0.5m; // % (market only)
    [ObservableProperty] private double _positionPercent;     // 0..100 helper

    // ---- Display strings -----------------------------------------------------
    [ObservableProperty] private string _priceDisplay = "Market: —";
    [ObservableProperty] private string _totalPriceDisplay = "Total: —";
    [ObservableProperty] private string _submitText = "Place Order";
    [ObservableProperty] private bool _canSubmit;

    private int? StockId => _selected.StockId;
    private decimal _lastPrice;

    public PlaceOrderViewModel(
        IMarketOrderService market,
        IUserOrderService orders,
        ISelectedStockService selected,
        ILogger<TradeViewModel> logger)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // sensible defaults
        SideIndex = 0;       // Buy
        OrderTypeIndex = 0;  // Limit
        UpdateOrderTypeFlags();

        // react to selection/price changes from ISelectedStockService
        _selected.PropertyChanged += OnSelectedChanged;

        RecomputeAll();
    }

    // Call this from your page (e.g., OnAppearing)
    public async Task InitializeAsync()
    {
        UpdateMarketPriceDisplay();
        RecomputeAll();
    }

    void OnSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ISelectedStockService.CurrentPrice))
        {
            if (_selected.CurrentPrice.HasValue)
            {
                _lastPrice = _selected.CurrentPrice.Value;
                UpdateMarketPriceDisplay();
                RecomputeAll();
            }
        }
        else if (e.PropertyName == nameof(ISelectedStockService.StockId))
        {
            // Optionally reset inputs when switching symbols
            Quantity = 0;
            LimitPrice = 0;
            PositionPercent = 0;
            RecomputeAll();
        }
    }

    // ---------------- change reactions from [ObservableProperty] --------------

    partial void OnOrderTypeIndexChanged(int value)
    {
        UpdateOrderTypeFlags();
        UpdateMarketPriceDisplay();
        RecomputeAll();
    }

    partial void OnSideIndexChanged(int value)
    {
        UpdateOrderTypeFlags();
        RecomputeAll();
    }

    partial void OnLimitPriceChanged(decimal value) => RecomputeAll();
    partial void OnQuantityChanged(int value) => RecomputeAll();
    partial void OnSlippagePercentChanged(decimal value) => RecomputeAll();
    partial void OnPositionPercentChanged(double value) => RecomputeAll();

    // ---------------- UI helpers ---------------------------------------------

    private void UpdateOrderTypeFlags()
    {
        IsLimitOrder = OrderTypeIndex == 0;
        IsMarketOrder = OrderTypeIndex == 1;
        var side = SideIndex == 0 ? "Buy" : "Sell";
        var type = OrderTypeIndex == 0 ? "Limit" : "Market";
        SubmitText = $"Place {type} {side}";
    }

    private void UpdateMarketPriceDisplay()
    {
        var culture = CultureInfo.CurrentCulture;
        PriceDisplay = IsMarketOrder
            ? $"Market: {_lastPrice.ToString("C", culture)}"
            : "Market: —";
    }

    private void RecomputeAll()
    {
        // Decide unit price depending on type + side (slippage applied for Market)
        var isBuy = SideIndex == 0;
        decimal unitPrice =
            IsLimitOrder ? LimitPrice
                         : ApplySlippage(_lastPrice, SlippagePercent, isBuy);

        // total
        var total = unitPrice * Quantity;
        var culture = CultureInfo.CurrentCulture;

        TotalPriceDisplay =
            (Quantity > 0 && unitPrice > 0)
                ? $"Total: {total.ToString("C", culture)}"
                : "Total: —";

        // Simple validation for enabling the submit button
        CanSubmit = StockId.HasValue
                    && Quantity > 0
                    && (IsMarketOrder ? _lastPrice > 0 : LimitPrice > 0);
    }

    private static decimal ApplySlippage(decimal basePrice, decimal slipPct, bool isBuy)
    {
        // BUY market: assume slightly worse (higher) fill,
        // SELL market: assume slightly worse (lower) fill
        var m = slipPct / 100m;
        if (m < 0) m = 0;
        return isBuy ? basePrice * (1m + m) : basePrice * (1m - m);
    }

    private async Task<decimal> SafeGetMarketPrice(int stockId)
    {
        try { return await _market.GetMarketPriceAsync(stockId); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get market price for {StockId}", stockId);
            return 0m;
        }
    }

    #region Place Order Commands
    [RelayCommand] private async Task PlaceOrder()
    {
        if (!CanSubmit || !StockId.HasValue) return;

        try
        {
            IsBusy = true;

            var stockId = StockId.Value;
            var isBuy = SideIndex == 0;

            OrderResult result;

            if (IsLimitOrder)
            {
                // exact price provided by user
                if (isBuy)
                    result = await _orders.PlaceLimitBuyOrderAsync(stockId, Quantity, LimitPrice);
                else
                    result = await _orders.PlaceLimitSellOrderAsync(stockId, Quantity, LimitPrice);
            }
            else
            {
                // market with guard rails: derive max/min price from slippage
                var guard = ApplySlippage(_lastPrice, SlippagePercent, isBuy);
                if (isBuy)
                    result = await _orders.PlaceMarketBuyOrderAsync(stockId, Quantity);
                else
                    result = await _orders.PlaceMarketSellOrderAsync(stockId, Quantity);
            }

            // Very light result handling (you can surface details in a toast/snackbar)
            if (result.Status != OrderStatus.Success)
            {
                await Shell.Current.DisplayAlert("Order Failed", result.ErrorMessage ?? "Unable to place order.", "OK");
                return;
            }

            // Reset just the quantity; keep the selected side/type for faster repeat
            Quantity = 0;
            RecomputeAll();

            await Shell.Current.DisplayAlert("Order Placed", "Your order was submitted.", "OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order");
            await Shell.Current.DisplayAlert("Error", "Unexpected error placing order.", "OK");
        }
        finally
        {
            IsBusy = false;
        }
    }
    #endregion
}
