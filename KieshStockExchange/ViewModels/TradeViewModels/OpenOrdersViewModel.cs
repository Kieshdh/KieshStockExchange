using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OpenOrdersViewModel : TradeTableViewModelBase<OpenOrderRow>
{
    #region Services and Constructors
    private readonly IOrderCacheService _cache;
    private readonly IOrderEntryService _orders;
    private readonly IUserPortfolioService _portfolio;
    private readonly IStockService _stocks;
    private readonly IAuthService _auth;
    private readonly IOrderEditService _editService;

    public OpenOrdersViewModel(ILogger<OpenOrdersViewModel> logger,
        IOrderCacheService cache, IOrderEntryService orders, IUserPortfolioService portfolio,
        IStockService stocks, IAuthService auth,
        ISelectedStockService selected, INotificationService notification, IOrderEditService editService)
        : base(selected, notification, logger)
    {
        _cache       = cache       ?? throw new ArgumentNullException(nameof(cache));
        _orders      = orders      ?? throw new ArgumentNullException(nameof(orders));
        _portfolio   = portfolio   ?? throw new ArgumentNullException(nameof(portfolio));
        _stocks      = stocks      ?? throw new ArgumentNullException(nameof(stocks));
        _auth        = auth        ?? throw new ArgumentNullException(nameof(auth));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));

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
        await RunBusyAsync(async () =>
        {
            await _cache.RefreshAsync(_auth.CurrentUserId);
            UpdateFromCache();
        }, ex => _logger.LogError(ex, "Error refreshing open orders."));
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
            // Re-pull funds + positions so PlaceOrderView's "Available shares" /
            // "Available funds" chip reflects the released reservation. Without
            // this the engine has correctly freed the reservation in cache + DB
            // but IUserPortfolioService's snapshot stays at the pre-cancel
            // numbers until the next user-driven refresh.
            await _portfolio.RefreshAsync(null);
            UpdateFromCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancel order failed for #{OrderId}", order.OrderId);
            await Shell.Current.DisplayAlert("Cancel failed", ex.Message, "OK");
        }
        finally { IsBusy = false; }
    }

    // Swap the right-hand panel into modify mode for this order. The actual
    // form lives in ModifyOrderView and is bound to ModifyOrderViewModel via
    // IOrderEditService — no modal, no navigation.
    [RelayCommand] private void Modify(Order order)
    {
        if (order is null) return;
        if (_editService.IsEditing) return;
        _editService.BeginEdit(order);
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
        var symbol = _stocks.SymbolOrDash(order.StockId);
        return new OpenOrderRow
        {
            Order = order,
            Symbol = symbol,
            ModifyCommand = ModifyCommand,
            CancelCommand = CancelCommand,
            GoToStockCommand = GoToStockCommand,
        };
    }

    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { PostUpdateFromCache(); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating open orders view."); }
    }
    #endregion
}
