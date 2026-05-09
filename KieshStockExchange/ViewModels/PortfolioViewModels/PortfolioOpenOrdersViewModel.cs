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
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioOpenOrdersViewModel : BaseViewModel
{
    private readonly IOrderCacheService  _cache;
    private readonly IOrderEntryService  _orders;
    private readonly IStockService       _stocks;
    private readonly IAuthService        _auth;
    private readonly ILogger<PortfolioOpenOrdersViewModel> _logger;

    [ObservableProperty] private ObservableCollection<OpenOrderRow> _currentView = new();

    public PortfolioOpenOrdersViewModel(
        IOrderCacheService  cache,
        IOrderEntryService  orders,
        IStockService       stocks,
        IAuthService        auth,
        ILogger<PortfolioOpenOrdersViewModel> logger)
    {
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
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

    // Mirrors OpenOrdersViewModel.ModifyAsync so the Portfolio page exposes the
    // same edit affordance the Trade page does. Validation is intentionally light
    // (positive price, qty ≥ filled, market orders can't change price); the engine
    // performs the canonical OrderValidator check inside ModifyOrderAsync.
    [RelayCommand]
    private async Task ModifyAsync(Order order)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var choice = await Shell.Current.DisplayActionSheet(
                "Modify order", "Cancel", null, "Price", "Quantity", "Price & Quantity");
            if (choice is null || choice == "Cancel") return;

            int? newQty = null;
            decimal? newPrice = null;

            if (choice.Contains("Price") && order.IsMarketOrder)
            {
                await Shell.Current.DisplayAlert("Modify not allowed",
                    "Cannot modify price of a market order.", "OK");
                return;
            }

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

            if (choice.Contains("Quantity"))
            {
                var qtyStr = await Shell.Current.DisplayPromptAsync(
                    "New quantity", $"Current: {order.Quantity}",
                    accept: "OK", cancel: "Back",
                    keyboard: Keyboard.Numeric);
                if (!int.TryParse(qtyStr, out var q) || q < order.AmountFilled)
                    throw new ArgumentException("Quantity must be ≥ filled amount.");
                if (q == order.Quantity && newPrice is null) return;
                newQty = q;
            }

            var result = await _orders.ModifyOrderAsync(_auth.CurrentUserId, order.OrderId, newQty, newPrice);
            _logger.LogInformation("Modify order #{OrderId}: {Status}", order.OrderId, result.Status);
            await _cache.RefreshAsync(_auth.CurrentUserId);
            RebuildView();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modify order failed for #{OrderId}", order.OrderId);
            await Shell.Current.DisplayAlert("Modify failed", ex.Message, "OK");
        }
        finally { IsBusy = false; }
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
