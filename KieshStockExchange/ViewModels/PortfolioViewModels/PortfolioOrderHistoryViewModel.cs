using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.UserServices;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioOrderHistoryViewModel : BaseViewModel
{
    private readonly IOrderCacheService _cache;
    private readonly IStockService      _stocks;
    private readonly IAuthService       _auth;
    private readonly ILogger<PortfolioOrderHistoryViewModel> _logger;

    [ObservableProperty] private ObservableCollection<ClosedOrderRow> _currentView = new();

    public PortfolioOrderHistoryViewModel(
        IOrderCacheService cache,
        IStockService      stocks,
        IAuthService       auth,
        ILogger<PortfolioOrderHistoryViewModel> logger)
    {
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _cache.OrdersChanged += OnOrdersChanged;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _cache.RefreshAsync(_auth.CurrentUserId);
            RebuildView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing order history.");
        }
        finally { IsBusy = false; }
    }

    private void RebuildView()
    {
        var rows = _cache.ClosedOrders
            .Where(o => o.StockId > 0)
            .OrderByDescending(o => o.UpdatedAt)
            .Select(CreateRow)
            .ToList();

        CurrentView = new ObservableCollection<ClosedOrderRow>(rows);
    }

    private ClosedOrderRow CreateRow(Order order)
    {
        if (!_stocks.TryGetSymbol(order.StockId, out string symbol))
            symbol = "-";
        return new ClosedOrderRow { Order = order, Symbol = symbol };
    }

    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(RebuildView); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating order history."); }
    }
}
