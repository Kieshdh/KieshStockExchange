using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Globalization;
using System.Reflection.Metadata;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class PlaceOrderViewModel : BaseViewModel
{
    #region Services
    private readonly IMarketOrderService _market;
    private readonly IUserOrderService _orders;
    private readonly ISelectedStockService _selected;
    private readonly ILogger<TradeViewModel> _logger;
    #endregion

    #region Observable properties
    [ObservableProperty] private int _selectedSideIndex; // 0 = Buy, 1 = Sell
    [ObservableProperty] private int _selectedOrderTypeIndex; // 0 = Market, 1 = Limit

    [ObservableProperty] private decimal _currentMarketPrice = 0m;
    [ObservableProperty] private string _currentMarketPriceDisplay;
    [ObservableProperty] private decimal _limitPrice;
    [ObservableProperty] private decimal _slippagePercent;

    [ObservableProperty] private int _quantity;
    [ObservableProperty] private decimal _orderValue;
    [ObservableProperty] private string _availableAssets;

    public string SubmitButtonText => SelectedSideIndex == 0 ? "BUY" : "SELL";
    public Color SubmitButtonColor => SelectedSideIndex == 0 ? Colors.ForestGreen : Colors.OrangeRed;
    #endregion

    #region Constructor and initialization
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

        if (_selected.CurrentPrice is { } p)
        {
            CurrentMarketPrice = p;
            CurrentMarketPriceDisplay = CurrencyHelper.Format(p, CurrencyType.USD);  
        }

        _selected.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_selected.CurrentPrice) && _selected.CurrentPrice is { } px)
            {
                CurrentMarketPriceDisplay = CurrencyHelper.Format(px, CurrencyType.USD);
                CurrentMarketPrice = px;
                UpdateOrderValue();
            }
                
        };

    }

    // Call this from your page (e.g., OnAppearing)
    public async Task InitializeAsync()
    {
        await LoadMarketPrice();
    }
    #endregion

    #region Commands and methods
    private async Task LoadMarketPrice()
    {
        // TODO: Replace with SelectedStockService
        CurrentMarketPrice = await _market.GetMarketPriceAsync(1);
    }

    [RelayCommand]
    private void SetSlippage(object? parameter)
    {
        if (ParsingHelper.TryToDecimal(parameter, out var value))
            SlippagePercent = value;
    }

    [RelayCommand] 
    private void SetQuantityPercent(object? parameter)
    {
        if (ParsingHelper.TryToDecimal(parameter, out var percent))
        {
            int max = 100; // TODO: Get from user's available assets 
        }
            
        Quantity = (int)((100 * percent) / 100);
        UpdateOrderValue();
    }

    private void UpdateOrderValue()
    {
        var price = SelectedOrderTypeIndex == 0 ? CurrentMarketPrice : LimitPrice;
        OrderValue = price * Quantity;
    }

    [RelayCommand] private async Task PlaceOrder()
    {
        if (!_selected.HasSelectedStock)
        {
            _logger.LogWarning("No stock selected for placing order.");
            return;
        }
        var stockId = _selected.StockId.Value;

        if (SelectedOrderTypeIndex == 0) // Market
        {
            var price = (SelectedSideIndex == 0) ?
                  CurrentMarketPrice * (1 + SlippagePercent / 100) 
                : CurrentMarketPrice * (1 - SlippagePercent / 100);
            if (SelectedSideIndex == 0)
                await _orders.PlaceMarketBuyOrderAsync(stockId, Quantity, price);
            else
                await _orders.PlaceMarketSellOrderAsync(stockId, Quantity, price);
        }
        else // Limit
        {
            if (SelectedSideIndex == 0)
                await _orders.PlaceLimitBuyOrderAsync(stockId, Quantity, LimitPrice);
            else
                await _orders.PlaceLimitSellOrderAsync(stockId, Quantity, LimitPrice);
        }
    }
    #endregion
}
