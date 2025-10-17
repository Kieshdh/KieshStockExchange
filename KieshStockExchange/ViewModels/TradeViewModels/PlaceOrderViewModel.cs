using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
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

    //[ObservableProperty]
    [ObservableProperty] private int _quantity = 0;
    [ObservableProperty] private decimal _limitPrice = 0m;
    [ObservableProperty] private decimal _slippagePrc = 0.005m; // 0.5% default

    [ObservableProperty] private string _submitButtonText = "Buy"; // Buy="Buy {Symbol}", Sell="Sell {Symbol}"
    [ObservableProperty] private Color _submitButtonColor = Colors.ForestGreen; // Buy=ForestGreen, Sell=OrangeRed
    [ObservableProperty] private string _orderValue = "-"; // Total order value based on quantity and price
    [ObservableProperty] private string _pricePreview = "-";  // For market orders: shows computed price with slippage
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
    partial void OnLimitPriceChanged(decimal value) { RecomputeUi(); }
    partial void OnSlippagePrcChanged(decimal value) { RecomputeUi(); }
    #endregion

    #region Services and Constructor
    private readonly IUserPortfolioService _portfolio;
    private readonly IUserOrderService _orders;
    private readonly ILogger<PlaceOrderViewModel> _logger;

    public PlaceOrderViewModel(IUserOrderService orders, IUserPortfolioService portfolio, 
        ILogger<PlaceOrderViewModel> logger, ISelectedStockService selected) : base(selected)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region StockAware Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency)
    {
        // Method called when selected stock changes
        // For limit orders, set limit price to current price
        if (IsLimitSelected)
            LimitPrice = Selected.CurrentPrice > 0 ? Selected.CurrentPrice : 0m;

        RecomputeUi();
        return Task.CompletedTask;
    }

    protected override Task OnPriceChangedAsync(int? stockId, CurrencyType currency, decimal price, DateTime? updatedAt)
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
        percent = Math.Clamp(percent, 0m, 100m);

        // Guard: no stock selected
        if (!Selected.HasSelectedStock) 
        { 
            Quantity = 0; 
            RecomputeUi(); 
            return; 
        }

        // Determine max quantity based on side
        int maxQty = 0;
        if (IsBuySelected) // Buy
        {
            var fund = _portfolio.GetFundByCurrency(Selected.Currency);
            if (fund == null || fund.AvailableBalance <= 0) maxQty = 0;
            else maxQty = (int)(fund.AvailableBalance / PriceForOrder);

        }
        else // Sell
        {
            var holding = _portfolio.GetPositionByStockId(Selected.StockId ?? -1);
            if (holding == null || holding.AvailableQuantity <= 0) maxQty = 0;
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
            var ok = ValidateInputs();
            if (!ok) return;


            var id = Selected.StockId!.Value; 
            var cur = Selected.Currency;
            var ct = CancellationToken.None;

            OrderResult result;
            if (IsMarketSelected)
            {
                _logger.LogInformation("Placing {Side} MARKET order for {Quantity} shares of {Symbol} " +
                    "at computed price {Price} {Currency} (slippage {Slippage:P2})",
                    IsBuySelected ? "BUY" : "SELL", Quantity, Selected.Symbol, PriceForOrder, cur, SlippagePrc);
                if (IsBuySelected)
                    result = await _orders.PlaceMarketBuyOrderAsync(id, Quantity, PriceForOrder, cur, ct);
                else
                    result = await _orders.PlaceMarketSellOrderAsync(id, Quantity, PriceForOrder, cur, ct);
            }
            else
            {
                _logger.LogInformation("Placing {Side} LIMIT order for {Quantity} shares of {Symbol} at limit price {Price} {Currency}",
                    IsBuySelected ? "BUY" : "SELL", Quantity, Selected.Symbol, LimitPrice, cur);
                if (IsBuySelected)
                    result = await _orders.PlaceLimitBuyOrderAsync(id, Quantity, LimitPrice, cur, ct);
                else
                    result = await _orders.PlaceLimitSellOrderAsync(id, Quantity, LimitPrice, cur, ct);
            }
            // Show result
            if (result.PlacedSuccesfully)
                _logger.LogInformation("{Order} {Message}", result.PlacedOrder, result.SuccesMessage);
            else _logger.LogWarning("Order placement failed: {ErrorMessage}", result.ErrorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error placing order for {Symbol}", Selected.Symbol);
        }
        finally { IsBusy = false; }
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
        
        // Price preview
        decimal price = IsMarketSelected ? Selected.CurrentPrice : LimitPrice;
        PricePreview = price > 0 ? CurrencyHelper.Format(price, Selected.Currency) : "-";

        // Order value preview
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
