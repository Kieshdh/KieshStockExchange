using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.UserServices;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;


namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OrderHistoryViewModel : StockAwareViewModel
{
    #region Properties
    [ObservableProperty] private ObservableCollection<ClosedOrderRow> _currentView = new();

    private bool ShowAll = false;

    public void SetShowAll(bool show)
    {
        if (ShowAll == show) return;
        ShowAll = show;
        UpdateFromCache();
    }
    #endregion

    #region Services and Constructor
    private readonly ILogger<OrderHistoryViewModel> _logger;
    private readonly IUserOrderService _orders;
    private readonly IStockService _stocks;
    private readonly IAuthService _auth;

    public OrderHistoryViewModel(ILogger<OrderHistoryViewModel> logger, 
        IUserOrderService orders, IStockService stocks, IAuthService auth,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));

        // Subscribe to order changes
        _orders.OrdersChanged += OnOrdersChanged;

        // Initial load
        InitializeSelection();
    }
    #endregion

    #region Abstract Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        UpdateFromCache(stockId, currency);
        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
        => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _orders.OrdersChanged -= OnOrdersChanged;
        base.Dispose(disposing);
    }
    #endregion

    #region Commands
    // Manual refresh command
    [RelayCommand] public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _orders.RefreshOrdersAsync(_auth.CurrentUserId);
            UpdateFromCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing order history.");
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Private methods
    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(() => UpdateFromCache()); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating order history."); }
    }

    private void UpdateFromCache(int? stockId = null, CurrencyType? currency = null)
    {
        // If no stock selected, clear view
        if (!Selected.HasSelectedStock)
        {
            CurrentView.Clear();
            return;
        }
        // Use selected stock if none provided
        stockId ??= Selected.StockId;
        currency ??= Selected.Currency;
        UpdateFromCache(stockId!.Value, currency.Value);
    }

    private void UpdateFromCache(int stockId, CurrencyType currency)
    {
        var snapshot = _orders.UserOpenOrders.ToList();
        var rows = new List<ClosedOrderRow>(capacity: snapshot.Count);

        if (stockId > 0)
        {
            // Get all orders for the current stock and currency
            var current = snapshot.Where(o => o.StockId == stockId && o.CurrencyType == currency);

            // Create OpenOrderRow objects and add to list
            foreach (var order in current.OrderByDescending(o => o.UpdatedAt))
                if (order.StockId > 0) rows.Add(CreateClosedOrderRow(order));
        }

        // If showing all, add other orders
        if (ShowAll)
            foreach (var o in snapshot.OrderByDescending(o => o.UpdatedAt))
            {
                if (o.StockId <= 0) continue;
                if (o.StockId == stockId && o.CurrencyType == currency) continue;
                rows.Add(CreateClosedOrderRow(o));
            }

        // Update the observable collection
        CurrentView = new ObservableCollection<ClosedOrderRow>(rows);
    }

    private ClosedOrderRow CreateClosedOrderRow(Order order)
    {
        if (!_stocks.TryGetSymbol(order.StockId, out string symbol))
            symbol = "-";
        return new ClosedOrderRow
        {
            Order = order,
            Symbol = symbol
        };
    }
    #endregion
}

public sealed class ClosedOrderRow
{
    public required Order Order { get; init; }
    public required string Symbol { get; init; }
    public string Opened => Order.CreatedDateShort;
    public string Closed => Order.UpdatedDateShort;
    public string Side => Order.SideDisplay;
    public string Type => Order.TypeDisplay;
    public string Qty => Order.AmountFilledDisplay;
    public string Price => Order.PriceDisplay;
    public string Total => Order.TotalAmountDisplay;
}
