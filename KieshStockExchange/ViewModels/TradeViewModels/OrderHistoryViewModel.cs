using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class OrderHistoryViewModel : TradeTableViewModelBase<ClosedOrderRow>
{
    #region Services and Constructor
    private readonly IOrderCacheService _cache;
    private readonly IStockService _stocks;
    private readonly IAuthService _auth;

    public OrderHistoryViewModel(ILogger<OrderHistoryViewModel> logger,
        IOrderCacheService cache, IStockService stocks, IAuthService auth,
        ISelectedStockService selected, INotificationService notification)
        : base(selected, notification, logger)
    {
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
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
            _logger.LogError(ex, "Error refreshing order history.");
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Row Building
    protected override IEnumerable<ClosedOrderRow> BuildRows(int stockId, CurrencyType currency)
    {
        var snapshot = _cache.ClosedOrders.ToList();

        if (stockId > 0)
        {
            foreach (var order in snapshot
                .Where(o => o.StockId == stockId && o.CurrencyType == currency)
                .OrderByDescending(o => o.UpdatedAt))
            {
                if (order.StockId > 0) yield return CreateClosedOrderRow(order);
            }
        }

        if (!ShowAll) yield break;

        foreach (var order in snapshot.OrderByDescending(o => o.UpdatedAt))
        {
            if (order.StockId <= 0) continue;
            if (order.StockId == stockId && order.CurrencyType == currency) continue;
            yield return CreateClosedOrderRow(order);
        }
    }

    private ClosedOrderRow CreateClosedOrderRow(Order order)
    {
        if (!_stocks.TryGetSymbol(order.StockId, out string symbol))
            symbol = "-";
        return new ClosedOrderRow { Order = order, Symbol = symbol };
    }

    private void OnOrdersChanged(object? s, EventArgs e)
    {
        try { PostUpdateFromCache(); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating order history."); }
    }
    #endregion
}

public sealed class ClosedOrderRow : ISideRow
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
    public bool IsBuyOrder => Order.IsBuyOrder;
    public bool IsSellOrder => Order.IsSellOrder;
}
