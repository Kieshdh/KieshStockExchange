using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.MarketEngineServices.Helpers;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class LevelRow : ObservableObject
{
    #region Properties
    private CurrencyType Currency;

    public LevelSide Side { get; }

    public decimal Price { get; set; }
    public string PriceDisplay => CurrencyHelper.Format(Price, Currency);

    [ObservableProperty] private int _quantity;
    [ObservableProperty] private int _CumQuantity;
    [ObservableProperty] private double _depthRatio;
    [ObservableProperty] private bool _isBestLevel;

    public string QuantityDisplay => Quantity.ToString("N0");
    public string CumQuantityDisplay => CumQuantity.ToString("N0");
    #endregion

    #region Constructor and Methods
    public LevelRow(decimal price, int quantity, int cumulative,
                    double depthRatio, bool isBestLevel,
                    CurrencyType currency, LevelSide side)
    {
        Side = side;
        Price = price;
        Quantity = quantity;
        CumQuantity = cumulative;
        DepthRatio = depthRatio;
        IsBestLevel = isBestLevel;
        SetCurrency(currency);
    }

    public void Update(decimal price, int quantity, int cumulative,
                       double depthRatio, bool isBestLevel, CurrencyType currency)
    {
        Quantity = quantity;
        CumQuantity = cumulative;
        Price = price;
        DepthRatio = depthRatio;
        IsBestLevel = isBestLevel;
        Currency = currency;   // rows persist across stock switches — refresh currency so a EUR book never renders $.
        OnPropertyChanged(nameof(PriceDisplay));
    }

    public void SetCurrency(CurrencyType currency)
    {
        Currency = currency;
        OnPropertyChanged(nameof(PriceDisplay));
    }

    partial void OnQuantityChanged(int value) => OnPropertyChanged(nameof(QuantityDisplay));
    partial void OnCumQuantityChanged(int value) => OnPropertyChanged(nameof(CumQuantityDisplay));
    #endregion
}
