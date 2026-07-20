using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioOpenOrdersViewModel
    : PortfolioTableViewModelBase<OpenOrderRow, Order>
{
    private readonly IOrderCacheService  _cache;
    private readonly IOrderEntryService  _orders;
    private readonly IStockService       _stocks;
    private readonly IAuthService        _auth;
    private readonly ISelectedStockService _selected;
    private readonly IOrderEditService   _editService;

    public PortfolioOpenOrdersViewModel(
        IOrderCacheService  cache,
        IOrderEntryService  orders,
        IStockService       stocks,
        IAuthService        auth,
        ISelectedStockService selected,
        IOrderEditService   editService,
        ILogger<PortfolioOpenOrdersViewModel> logger)
        : base(logger)
    {
        _cache       = cache       ?? throw new ArgumentNullException(nameof(cache));
        _orders      = orders      ?? throw new ArgumentNullException(nameof(orders));
        _stocks      = stocks      ?? throw new ArgumentNullException(nameof(stocks));
        _auth        = auth        ?? throw new ArgumentNullException(nameof(auth));
        _selected    = selected    ?? throw new ArgumentNullException(nameof(selected));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));

        Subscribe();
    }

    protected override IEnumerable<Order> Source => _cache.OpenOrders;
    protected override int GetStockId(Order order) => order.StockId;
    protected override DateTime GetSortKey(Order order) => order.UpdatedAt;

    protected override OpenOrderRow CreateRow(Order order)
    {
        var symbol = _stocks.SymbolOrDash(order.StockId);
        return new OpenOrderRow
        {
            Order = order,
            Symbol = symbol,
            ModifyCommand = ModifyCommand,
            CancelCommand = CancelCommand,
        };
    }

    protected override Task RefreshSourceAsync() => _cache.RefreshAsync(_auth.CurrentUserId);

    protected override void Subscribe()   => _cache.OrdersChanged += OnSourceChanged;
    protected override void Unsubscribe() => _cache.OrdersChanged -= OnSourceChanged;

    protected override string RefreshErrorMessage => "Error refreshing open orders.";
    protected override string UpdateErrorMessage  => "Error updating open orders.";

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
}
