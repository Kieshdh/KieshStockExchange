using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class ChartViewModel : BaseViewModel
{
    private readonly ISelectedStockService _stockService;
    private readonly ILogger<TradeViewModel> _logger;

    [ObservableProperty] private string _stockCurrentPrice;

    public ChartViewModel(
        ISelectedStockService stockService,
        ILogger<TradeViewModel> logger)
    {
        _stockService = stockService ??
            throw new ArgumentNullException(nameof(stockService), "ISelectedStockContext cannot be null.");
        _logger = logger ??
            throw new ArgumentNullException(nameof(logger), "Logger cannot be null.");

        if (_stockService.CurrentPrice is { } p) _stockCurrentPrice = p.ToString("F2");

        stockService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(stockService.CurrentPrice) && stockService.CurrentPrice is { } px)
                StockCurrentPrice = px.ToString("F2");
        };
    }

    public async Task InitializeAsync()
    { 
        
    }
}