using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class ChartViewModel : BaseViewModel
{
    private readonly ISelectedStockService _selected;
    private readonly ILogger<TradeViewModel> _logger;

    [ObservableProperty] private string _currentPrice = String.Empty;

    public ChartViewModel(
        ISelectedStockService selected,
        ILogger<TradeViewModel> logger)
    {
        _selected = selected ??
            throw new ArgumentNullException(nameof(selected), "ISelectedStockContext cannot be null.");
        _logger = logger ??
            throw new ArgumentNullException(nameof(logger), "Logger cannot be null.");

        if (_selected.CurrentPrice is { } p) 
            CurrentPrice = CurrencyHelper.Format(p, CurrencyType.USD);

        _selected.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_selected.CurrentPrice) && _selected.CurrentPrice is { } px)
                CurrentPrice = CurrencyHelper.Format(px, CurrencyType.USD);
        };
    }

    public async Task InitializeAsync()
    { 
        
    }
}