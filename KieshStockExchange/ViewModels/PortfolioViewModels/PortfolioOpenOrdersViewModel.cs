using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioOpenOrdersViewModel : BaseViewModel
{
    private readonly IOrderCacheService  _cache;
    private readonly IOrderEntryService  _orders;
    private readonly IStockService       _stocks;
    private readonly IAuthService        _auth;
    private readonly ISelectedStockService _selected;
    private readonly IOrderEditService   _editService;
    private readonly ILogger<PortfolioOpenOrdersViewModel> _logger;

    [ObservableProperty] private ObservableCollection<OpenOrderRow> _currentView = new();

    public PortfolioOpenOrdersViewModel(
        IOrderCacheService  cache,
        IOrderEntryService  orders,
        IStockService       stocks,
        IAuthService        auth,
        ISelectedStockService selected,
        IOrderEditService   editService,
        ILogger<PortfolioOpenOrdersViewModel> logger)
    {
        _cache       = cache       ?? throw new ArgumentNullException(nameof(cache));
        _orders      = orders      ?? throw new ArgumentNullException(nameof(orders));
        _stocks      = stocks      ?? throw new ArgumentNullException(nameof(stocks));
        _auth        = auth        ?? throw new ArgumentNullException(nameof(auth));
        _selected    = selected    ?? throw new ArgumentNullException(nameof(selected));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));

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

    // Modify lives on the Trade page (the inline ModifyOrderView panel that
    // swaps in for PlaceOrderView). From the portfolio, navigate to TradePage
    // for the order's stock, then enter edit mode on arrival.
    [RelayCommand]
    private async Task Modify(Order order)
    {
        if (order is null) return;
        if (_editService.IsEditing) return;
        try
        {
            await _selected.Set(order.StockId, order.CurrencyType).ConfigureAwait(false);
            await Shell.Current.GoToAsync("///TradePage").ConfigureAwait(false);
            _editService.BeginEdit(order);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to Trade page for modify on #{OrderId}.", order.OrderId);
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
        return new OpenOrderRow
        {
            Order = order,
            Symbol = symbol,
            ModifyCommand = ModifyCommand,
            CancelCommand = CancelCommand,
        };
    }

    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(RebuildView); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating open orders."); }
    }
}
