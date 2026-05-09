using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using KieshStockExchange.Views.TradePageViews;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioOpenOrdersViewModel : BaseViewModel
{
    private readonly IOrderCacheService  _cache;
    private readonly IOrderEntryService  _orders;
    private readonly IStockService       _stocks;
    private readonly IAuthService        _auth;
    private readonly IServiceProvider    _services;
    private readonly ILogger<PortfolioOpenOrdersViewModel> _logger;

    [ObservableProperty] private ObservableCollection<OpenOrderRow> _currentView = new();

    public PortfolioOpenOrdersViewModel(
        IOrderCacheService  cache,
        IOrderEntryService  orders,
        IStockService       stocks,
        IAuthService        auth,
        IServiceProvider    services,
        ILogger<PortfolioOpenOrdersViewModel> logger)
    {
        _cache    = cache    ?? throw new ArgumentNullException(nameof(cache));
        _orders   = orders   ?? throw new ArgumentNullException(nameof(orders));
        _stocks   = stocks   ?? throw new ArgumentNullException(nameof(stocks));
        _auth     = auth     ?? throw new ArgumentNullException(nameof(auth));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));

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
            _logger.LogError(ex, "Error refreshing open orders.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CancelAsync(Order order)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _orders.CancelOrderAsync(_auth.CurrentUserId, order.OrderId);
            _logger.LogInformation("Cancel order #{OrderId}: {Status}", order.OrderId, result.Status);
            await _cache.RefreshAsync(_auth.CurrentUserId);
            RebuildView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel order failed for #{OrderId}", order.OrderId);
            await Shell.Current.DisplayAlert("Cancel failed", ex.Message, "OK");
        }
        finally { IsBusy = false; }
    }

    // Open the Modify Order popup as a Shell modal page (Binance-style). Modal
    // navigation keeps the popup on top within the same window so it can't
    // slip behind chart hover. ModifyOrderPage.IsOpen prevents a double-tap
    // from stacking two modals.
    [RelayCommand]
    private async Task Modify(Order order)
    {
        if (order is null) return;
        if (ModifyOrderPage.IsOpen) return;
        try
        {
            var page = _services.GetRequiredService<ModifyOrderPage>();
            page.Initialize(order);
            page.Disappearing += (_, __) =>
                MainThread.BeginInvokeOnMainThread(RebuildView);
            await Shell.Current.Navigation.PushModalAsync(page).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Modify Order popup for #{OrderId}.", order.OrderId);
        }
    }

    private void RebuildView()
    {
        var rows = _cache.OpenOrders
            .Where(o => o.StockId > 0)
            .OrderByDescending(o => o.UpdatedAt)
            .Select(CreateRow)
            .ToList();

        CurrentView = new ObservableCollection<OpenOrderRow>(rows);
    }

    private OpenOrderRow CreateRow(Order order)
    {
        if (!_stocks.TryGetSymbol(order.StockId, out string symbol))
            symbol = "-";
        return new OpenOrderRow { Order = order, Symbol = symbol };
    }

    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(RebuildView); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating open orders."); }
    }
}
