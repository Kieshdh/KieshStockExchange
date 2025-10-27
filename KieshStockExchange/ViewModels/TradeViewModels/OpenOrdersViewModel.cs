using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OpenOrdersViewModel : StockAwareViewModel
{
    #region Properties
    private List<Order> CurrentStockOrders = new();
    private List<Order> OtherOpenOrders = new();

    [ObservableProperty] private ObservableCollection<OpenOrderRow> _currentOrdersView = new();

    [ObservableProperty] private bool _showAllOrders = false;

    public bool IsNotBusy => !IsBusy;
    #endregion

    #region Services and Constructors
    private readonly IUserOrderService _orders;
    private readonly IStockService _stocks;
    private readonly ILogger<OpenOrdersViewModel> _logger;

    public OpenOrdersViewModel(IUserOrderService orders, ILogger<OpenOrdersViewModel> logger, 
        ISelectedStockService selected, IStockService stocks) : base(selected)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));

        InitializeSelection();
    }
    #endregion

    #region Abstract Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        UpdateFromCache(stockId, currency);
        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
        => Task.CompletedTask;
    #endregion

    [RelayCommand] public async Task RefreshAsync()
    {
        await _orders.RefreshOrdersAsync();
        UpdateFromCache();
    }

    private void UpdateFromCache(int? stockId = null, CurrencyType? currency = null)
    {
        // Use selected stock and currency if not provided
        stockId ??= Selected.StockId;
        currency ??= Selected.Currency;

        // Filter and sort orders
        CurrentStockOrders = _orders.UserOpenOrders
            .Where(o => o.StockId == (stockId ?? 0) && o.CurrencyType == currency)
            .OrderByDescending(o => o.CreatedAt).ToList();
        OtherOpenOrders = _orders.UserOpenOrders
            .Where(o => o.StockId != (stockId ?? 0) || o.CurrencyType != currency)
            .OrderByDescending(o => o.CreatedAt).ToList();

        // Update the observable collection
        CurrentOrdersView.Clear();

        // Add current stock orders first
        foreach (var order in CurrentStockOrders)
            if (order.StockId != 0)
                CurrentOrdersView.Add(CreateOpenOrderRow(order));

        // If showing all orders, add the others too
        if (!ShowAllOrders) return;
        foreach (var order in OtherOpenOrders)
            if (order.StockId != 0)
                CurrentOrdersView.Add(CreateOpenOrderRow(order));


    }

    private OpenOrderRow CreateOpenOrderRow(Order order)
    {
        if (!_stocks.TryGetSymbol(order.OrderId, out string symbol))
            symbol = "-";
        return new OpenOrderRow
        {
            Order = order,
            Symbol = symbol
        };
    }

    [RelayCommand] private async Task CancelAsync(Order order)
    {
        try
        {
            var result = await _orders.CancelOrderAsync(order.OrderId);
            _logger.LogInformation("Cancel order #{OrderId}: {Status}", order.OrderId, result.Status);
            await Refresh(); // re-pull + re-filter into CurrentOrdersView
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel order failed for #{OrderId}", order.OrderId);
        }
    }
}

public sealed class OpenOrderRow
{
    public required Order Order { get; init; }
    public required string Symbol { get; init; }
    public string Side => Order.SideDisplay;
    public string Type => Order.TypeDisplay;
    public string Qty => Order.AmountFilledDisplay;
    public string Price => Order.PriceDisplay;
    public string When => Order.CreatedDateShort;
}