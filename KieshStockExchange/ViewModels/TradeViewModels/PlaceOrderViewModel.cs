﻿using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection.Metadata;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class PlaceOrderViewModel : StockAwareViewModel
{
    #region Observable properties
    [ObservableProperty] private int _selectedSideIndex = 0; // 0 = Buy, 1 = Sell
    [ObservableProperty] private int _selectedTypeIndex = 0; // 0 = Market, 1 = Limit

    [ObservableProperty] private int _quantity = 0;
    [ObservableProperty] private string _limitPriceString = String.Empty;
    [ObservableProperty] private decimal _slippagePrc = 0.005m; // 0.5% default
    [ObservableProperty] private string _assetText = "Available Funds"; // Buy="Available Funds", Sell="Available Shares"
    [ObservableProperty] private string _availableAssetsText = "-"; // Based on side and portfolio
    [ObservableProperty] private string _orderValue = "-"; // Total order value based on quantity and price

    [ObservableProperty] private string _submitButtonText = "Buy"; // Buy="Buy {Symbol}", Sell="Sell {Symbol}"
    [ObservableProperty] private Color _submitButtonColor = Colors.ForestGreen; // Buy=ForestGreen, Sell=OrangeRed

    private decimal LimitPrice => ParsingHelper.TryToDecimal(_limitPriceString, out var val) ? val : 0m;
    #endregion

    #region PropertyChanged events
    partial void OnSelectedSideIndexChanged(int value) 
    { 
        RecomputeUi(); 
        OnPropertyChanged(nameof(IsBuySelected));
        OnPropertyChanged(nameof(IsSellSelected));
    }
    partial void OnSelectedTypeIndexChanged(int value)
    {
        RecomputeUi();
        OnPropertyChanged(nameof(IsMarketSelected));
        OnPropertyChanged(nameof(IsLimitSelected));
    }
    partial void OnQuantityChanged(int value) { RecomputeUi(); }
    partial void OnLimitPriceStringChanged(string value) { RecomputeUi(); }
    partial void OnSlippagePrcChanged(decimal value) { RecomputeUi(); }
    #endregion

    #region Assets display properties
    private Fund UserFund => _portfolio?.GetFundByCurrency(Selected.Currency) 
        ?? new Fund { CurrencyType = Selected.Currency };

    private Position UserPosition => _portfolio?.GetPositionByStockId(Selected.StockId ?? -1) 
        ?? new Position { StockId = Selected.StockId ?? 0 };

    private IDispatcherTimer? _assetsTimer;
    private EventHandler? _assetsTickHandler;
    private bool _assetsRefreshRunning = false;

    public TimeSpan AssetsRefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    #endregion

    #region Services and Constructor
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserOrderService _orders;
    private readonly ILogger<PlaceOrderViewModel> _logger;
    private readonly INotificationService _notification;
    private readonly IDispatcher _dispatcher;

    public PlaceOrderViewModel(IUserOrderService orders, IUserPortfolioService portfolio, 
        INotificationService notification, ILogger<PlaceOrderViewModel> logger, 
        ISelectedStockService selected, IDispatcher disp) : base(selected)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = disp ?? throw new ArgumentNullException(nameof(disp));
        _notification = notification ?? throw new ArgumentNullException(nameof(notification));

        InitializeSelection();
        StartAssetsAutoRefresh();
    }
    #endregion

    #region StockAware Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        // Method called when selected stock changes
        // For limit orders, set limit price to current price
        if (IsLimitSelected)
            LimitPriceString = Selected.CurrentPriceDisplay;

        RecomputeUi();
        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedsync(int? stockId, CurrencyType currency, 
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        // Price moved -> update order value preview
        RecomputeUi();
        return Task.CompletedTask;
    }
    #endregion

    #region RelayCommands
    [RelayCommand] private void SetSlippage(object? parameter)
    {
        if (ParsingHelper.TryToDecimal(parameter, out var value))
            SlippagePrc = value;
    }

    [RelayCommand] private void SetQuantityPercent(object? parameter)
    {
        // Parse percentage
        if (!ParsingHelper.TryToDecimal(parameter, out var percent))
            return;
        percent = Math.Clamp(percent, 0m, 100m); // Clamp to [0,100]

        // Guard: no stock selected
        if (!Selected.HasSelectedStock)
        {
            Quantity = 0;
            RecomputeUi();
            return;
        }

        // Determine max quantity based on side
        int maxQty;
        if (IsBuySelected) // Buy
        {
            var fund = _portfolio.GetFundByCurrency(Selected.Currency);
            var price = PriceForOrder;
            if (fund == null || fund.AvailableBalance <= 0 || price <= 0m) 
                maxQty = 0;
            else maxQty = (int)(fund.AvailableBalance / price);

        }
        else // Sell
        {
            var holding = _portfolio.GetPositionByStockId(Selected.StockId ?? -1);
            if (holding == null || holding.AvailableQuantity <= 0) 
                maxQty = 0;
            else maxQty = holding.AvailableQuantity;
        }

        // Compute and set quantity
        Quantity = (int)(maxQty * (percent / 100m));
        RecomputeUi();
    }

    [RelayCommand] private async Task PlaceOrderAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        try
        {
            if (!ValidateInputs())
            {
                _logger.LogWarning("Order validation failed for {Symbol}. Order not placed.", Selected.Symbol);
                return;
            }

            var id = Selected.StockId!.Value; 
            var cur = Selected.Currency;
            var ct = CancellationToken.None;

            OrderResult result;
            if (IsMarketSelected)
            {
                _logger.LogInformation("Placing {Side} MARKET order for {Quantity} shares of {Symbol} " +
                    "at computed price {Price} {Currency} (slippage {Slippage:P2})",
                    IsBuySelected ? "BUY" : "SELL", Quantity, Selected.Symbol, PriceForOrder, cur, SlippagePrc);
                
                result = IsBuySelected
                    ? await _orders.PlaceMarketBuyOrderAsync(id, Quantity, PriceForOrder, cur, ct)
                    : await _orders.PlaceMarketSellOrderAsync(id, Quantity, PriceForOrder, cur, ct);
            }
            else
            {
                _logger.LogInformation("Placing {Side} LIMIT order for {Quantity} shares of {Symbol} at limit price {Price} {Currency}",
                    IsBuySelected ? "BUY" : "SELL", Quantity, Selected.Symbol, LimitPrice, cur);
                
                result = IsBuySelected
                    ? await _orders.PlaceLimitBuyOrderAsync(id, Quantity, LimitPrice, cur, ct)
                    : await _orders.PlaceLimitSellOrderAsync(id, Quantity, LimitPrice, cur, ct);
            }

            // Show result
            await _notification.NotifyOrderResultAsync(result, ct);

            // Log result
            if (result.PlacedSuccessfully)
                _logger.LogInformation("Order placed. Id={Id}, Remaining={Rem}, Fills={Fills}, AvgPrice={Avg}",
                result.NewOrderId, result.RemainingQuantity, result.TotalFilledQuantity, result.AverageFillPrice);
            else
                _logger.LogWarning("Order failed: {Message}", result.ErrorMessage);

            // Refresh assets
            await UpdateAssetsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order for {Symbol}", Selected.Symbol);
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Assets Auto-Refresh and disposal
    private async Task UpdateAssetsAsync()
    {
        // Refresh portfolio after order placement
        try { await _portfolio.RefreshAsync(null, CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Error refreshing portfolio after order placement."); }
    }

    private void StartAssetsAutoRefresh()
    {
        // If already started, skip
        if (_assetsTimer != null) return;

        // Create and start timer
        _assetsTimer = _dispatcher.CreateTimer();
        _assetsTimer.Interval = AssetsRefreshInterval;

        // Creaet Tick handler
        _assetsTickHandler = async (s, e) =>
        {
            if (_assetsRefreshRunning) return;
            _assetsRefreshRunning = true;
            try 
            { 
                await UpdateAssetsAsync();
                RecomputeUi();
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Assets auto-refresh tick failed.");
            }
            finally { _assetsRefreshRunning = false; }
        };

        // Attach handler and start
        _assetsTimer.Tick += _assetsTickHandler;
        _assetsTimer.Start();

        // Initial update
        _ = UpdateAssetsAsync().ContinueWith(_ => RecomputeUi());
    }

    private void StopAssetsAutoRefresh()
    {
        // If not started, skip
        if (_assetsTimer == null) return;

        // Detach handler and stop timer
        try
        {
            if (_assetsTickHandler != null)
            {
                _assetsTimer.Tick -= _assetsTickHandler;
                _assetsTickHandler = null;
            }
            _assetsTimer.Stop();
        }
        finally { _assetsTimer = null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            StopAssetsAutoRefresh();
        base.Dispose(disposing);
    }
    #endregion

    #region Helpers variables
    public bool IsMarketSelected => SelectedTypeIndex == 0;
    public bool IsLimitSelected => SelectedTypeIndex == 1;
    public bool IsBuySelected => SelectedSideIndex == 0;
    public bool IsSellSelected => SelectedSideIndex == 1;
    private decimal PriceForOrder => IsMarketSelected ? ComputeMarketGuardPrice() : LimitPrice;
    #endregion

    #region Private Methods
    private void RecomputeUi()
    {
        // Submit button text and color
        SubmitButtonText = IsBuySelected ? $"Buy {Selected.Symbol}" : $"Sell {Selected.Symbol}";
        SubmitButtonColor = IsBuySelected ? Colors.ForestGreen : Colors.OrangeRed;

        // User Asset Display
        AssetText = IsBuySelected ? "Available Funds" : "Available shares";
        AvailableAssetsText = IsBuySelected
            ? (UserFund.AvailableBalance > 0 ? UserFund.AvailableBalanceDisplay : "-")
            : ($"{UserPosition.AvailableQuantity} {Selected.Symbol}");

        // Order value preview
        decimal price = IsMarketSelected ? Selected.CurrentPrice : LimitPrice;
        var total = (price > 0 && Quantity > 0) ? price * Quantity : 0m;
        OrderValue = total > 0 ? CurrencyHelper.Format(total, Selected.Currency) : "-";
    }

    private bool ValidateInputs()
    {

        if (!Selected.HasSelectedStock)
        {
            _logger.LogWarning("No stock selected.");
            return false;
        }
        if (Quantity <= 0)
        {
            _logger.LogWarning("Quantity must be positive.");
            return false;
        }
        if (IsMarketSelected && Selected.CurrentPrice <= 0)
        {
            _logger.LogWarning("No market price available for market order.");
            return false;
        }
        if (PriceForOrder <= 0)
        {
            _logger.LogWarning("Invalid price.");
            return false;
        }
        return true;
    }

    private decimal ComputeMarketGuardPrice()
    {
        // For market: cap (buy) or floor (sell) to protect from bad ticks
        var prc = Math.Max(0m, SlippagePrc);
        return IsBuySelected ? Selected.CurrentPrice * (1m + prc) : Selected.CurrentPrice * (1m - prc);
    }
    #endregion
}
