using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.Views.TradePageViews;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OpenOrdersViewModel : TradeTableViewModelBase<OpenOrderRow>
{
    #region Services and Constructors
    private readonly IOrderCacheService _cache;
    private readonly IOrderEntryService _orders;
    private readonly IStockService _stocks;
    private readonly ILogger<OpenOrdersViewModel> _logger;
    private readonly IAuthService _auth;
    private readonly IServiceProvider _services;

    public OpenOrdersViewModel(ILogger<OpenOrdersViewModel> logger,
        IOrderCacheService cache, IOrderEntryService orders, IStockService stocks, IAuthService auth,
        ISelectedStockService selected, INotificationService notification, IServiceProvider services)
        : base(selected, notification, logger)
    {
        _cache    = cache    ?? throw new ArgumentNullException(nameof(cache));
        _orders   = orders   ?? throw new ArgumentNullException(nameof(orders));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        _stocks   = stocks   ?? throw new ArgumentNullException(nameof(stocks));
        _auth     = auth     ?? throw new ArgumentNullException(nameof(auth));
        _services = services ?? throw new ArgumentNullException(nameof(services));

        _cache.OrdersChanged += OnOrdersChanged;
        InitializeSelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cache.OrdersChanged -= OnOrdersChanged;
        base.Dispose(disposing);
    }
    #endregion

    #region Commands
    [RelayCommand] public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _cache.RefreshAsync(_auth.CurrentUserId);
            UpdateFromCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing open orders.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private async Task CancelAsync(Order order)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _orders.CancelOrderAsync(_auth.CurrentUserId, order.OrderId);
            _logger.LogInformation("Cancel order #{OrderId}: {Status}", order.OrderId, result.Status);
            await _cache.RefreshAsync(_auth.CurrentUserId);
            UpdateFromCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel order failed for #{OrderId}", order.OrderId);
            await Shell.Current.DisplayAlert("Cancel failed", ex.Message, "OK");
        }
        finally { IsBusy = false; }
    }

    // Open the Modify Order popup window (Binance-style). The VM does the work
    // on Confirm; we just construct the page here and pass it the target order.
    [RelayCommand] private void Modify(Order order)
    {
        if (order is null) return;
        var page = _services.GetRequiredService<ModifyOrderPage>();
        page.Initialize(order);
        var window = new Window(page) { Title = "Modify order", Width = 460, Height = 420 };
        // Refresh on close so a successful modify shows up in this list immediately
        // even if the popup's RefreshAsync raced ahead of the OrdersChanged hop.
        // UpdateFromCache has optional params, so wrap in a parameterless lambda
        // for BeginInvokeOnMainThread's Action overload.
        window.Destroying += (_, __) => MainThread.BeginInvokeOnMainThread(() => UpdateFromCache());
        Application.Current?.OpenWindow(window);
    }
    #endregion

    #region Row Building
    protected override IEnumerable<OpenOrderRow> BuildRows(int stockId, CurrencyType currency)
    {
        var snapshot = _cache.OpenOrders.ToList();

        if (stockId > 0)
        {
            foreach (var order in snapshot
                .Where(o => o.StockId == stockId && o.CurrencyType == currency)
                .OrderByDescending(o => o.UpdatedAt))
            {
                if (order.StockId > 0) yield return CreateOpenOrderRow(order);
            }
        }

        if (!ShowAll) yield break;

        foreach (var order in snapshot.OrderByDescending(o => o.UpdatedAt))
        {
            if (order.StockId <= 0) continue;
            if (order.StockId == stockId && order.CurrencyType == currency) continue;
            yield return CreateOpenOrderRow(order);
        }
    }

    private OpenOrderRow CreateOpenOrderRow(Order order)
    {
        if (!_stocks.TryGetSymbol(order.StockId, out string symbol))
            symbol = "-";
        return new OpenOrderRow { Order = order, Symbol = symbol };
    }

    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { PostUpdateFromCache(); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating open orders view."); }
    }
    #endregion
}

public sealed class OpenOrderRow
{
    public required Order Order { get; init; }
    public required string Symbol { get; init; }
    public string When => Order.CreatedDateShort;
    public string Side => Order.SideDisplay;
    public string Type => Order.TypeDisplay;
    public string Qty => Order.AmountFilledDisplay;
    public string Price => Order.PriceDisplay;
    public string Total => Order.TotalAmountDisplay;
    public bool IsBuyOrder => Order.IsBuyOrder;
    public bool IsSellOrder => Order.IsSellOrder;
}
