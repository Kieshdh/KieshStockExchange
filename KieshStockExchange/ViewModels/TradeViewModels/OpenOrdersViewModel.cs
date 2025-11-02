using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OpenOrdersViewModel : StockAwareViewModel
{
    #region Properties
    [ObservableProperty] private ObservableCollection<OpenOrderRow> _currentView = new();

    private bool ShowAll = false;

    public void SetShowAll(bool show)
    {
        if (ShowAll == show) return;
        ShowAll = show;
        UpdateFromCache();
    }
    #endregion

    #region Services and Constructors
    private readonly IUserOrderService _orders;
    private readonly IStockService _stocks;
    private readonly ILogger<OpenOrdersViewModel> _logger;
    private readonly IAuthService _auth;

    public OpenOrdersViewModel(ILogger<OpenOrdersViewModel> logger, 
        IUserOrderService orders, IStockService stocks, IAuthService auth,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            var result = await _orders.CancelOrderAsync(order.OrderId);
            _logger.LogInformation("Cancel order #{OrderId}: {Status}", order.OrderId, result.Status);
            await RefreshAsync(); // re-pull + re-filter into CurrentOrdersView
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel order failed for #{OrderId}", order.OrderId);
            await Shell.Current.DisplayAlert("Cancel failed", ex.Message, "OK");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private async Task ModifyAsync(Order order)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // Strategy: let users change price, quantity, or both with quick prompts.
            var choice = await Shell.Current.DisplayActionSheet(
                "Modify order", "Cancel", null, "Price", "Quantity", "Price & Quantity");
            if (choice is null || choice == "Cancel") return;

            int? newQty = null;
            decimal? newPrice = null;

            // Cannot change price of market orders
            if (choice.Contains("Price") && order.IsMarketOrder)
            {
                await Shell.Current.DisplayAlert("Modify not allowed",
                    "Cannot modify price of a market order.", "OK");
                return;
            }

            // Change price
            if (choice.Contains("Price"))
            {
                var priceStr = await Shell.Current.DisplayPromptAsync(
                    "New limit price", $"Current: {order.PriceDisplay}",
                    accept: "OK", cancel: "Back",
                    keyboard: Keyboard.Numeric);
                if (string.IsNullOrWhiteSpace(priceStr)) return;

                var parsed = CurrencyHelper.Parse(priceStr, order.CurrencyType);
                if (!parsed.HasValue || parsed.Value <= 0m)
                    throw new ArgumentException("Invalid price.");
                newPrice = parsed.Value;
            }

            // Change quantity
            if (choice.Contains("Quantity"))
            {
                var qtyStr = await Shell.Current.DisplayPromptAsync(
                    "New quantity", $"Current: {order.Quantity}",
                    accept: "OK", cancel: "Back",
                    keyboard: Keyboard.Numeric);
                if (!int.TryParse(qtyStr, out var q) || q < order.AmountFilled)
                    throw new ArgumentException("Quantity must be ≥ filled amount.");
                if (q == order.Quantity && newPrice is null) return; // nothing changed
                newQty = q;
            }

            // Update the order in the service
            var result = await _orders.ModifyOrderAsync(order.OrderId, newQty, newPrice);

            _logger.LogInformation("Modify order #{OrderId}: {Status}", order.OrderId, result.Status);
            await RefreshAsync(); // re-pull + re-filter into CurrentOrdersView
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modify order failed for #{OrderId}", order.OrderId);
            await Shell.Current.DisplayAlert("Modify failed", ex.Message, "OK");
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Private Methods
    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(() => UpdateFromCache()); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating open orders view."); }
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
        var rows = new List<OpenOrderRow>(capacity: snapshot.Count);

        if (stockId > 0)
        {
            // Get all orders for the current stock and currency
            var current = snapshot.Where(o => o.StockId == stockId && o.CurrencyType == currency);

            // Create OpenOrderRow objects and add to list
            foreach (var order in current.OrderByDescending(o => o.UpdatedAt))
                if (order.StockId > 0) rows.Add(CreateOpenOrderRow(order));
        }

        // If showing all, add other orders
        if (ShowAll) 
            foreach (var o in snapshot.OrderByDescending(o => o.UpdatedAt))
            {
                if (o.StockId <= 0) continue;
                if (o.StockId == stockId && o.CurrencyType == currency) continue;
                rows.Add(CreateOpenOrderRow(o));
            }

        // Update the observable collection
        CurrentView = new ObservableCollection<OpenOrderRow>(rows);
    }

    private OpenOrderRow CreateOpenOrderRow(Order order)
    {
        if (!_stocks.TryGetSymbol(order.StockId, out string symbol))
            symbol = "-";
        return new OpenOrderRow
        {
            Order = order,
            Symbol = symbol
        };
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
}