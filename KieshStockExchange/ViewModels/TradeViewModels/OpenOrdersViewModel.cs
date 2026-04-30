using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketEngineServices;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.UserServices;
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

    public OpenOrdersViewModel(ILogger<OpenOrdersViewModel> logger,
        IOrderCacheService cache, IOrderEntryService orders, IStockService stocks, IAuthService auth,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));

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

    [RelayCommand] private async Task ModifyAsync(Order order)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // let users change price, quantity, or both with quick prompts.
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
            UpdateFromCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modify order failed for #{OrderId}", order.OrderId);
            await Shell.Current.DisplayAlert("Modify failed", ex.Message, "OK");
        }
        finally { IsBusy = false; }
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
