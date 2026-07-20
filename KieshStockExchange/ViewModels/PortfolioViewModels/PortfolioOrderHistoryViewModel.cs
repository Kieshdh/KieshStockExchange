using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using KieshStockExchange.ViewModels.TradeViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.PortfolioViewModels;

public partial class PortfolioOrderHistoryViewModel
    : PortfolioTableViewModelBase<ClosedOrderRow, Order>
{
    private readonly IOrderCacheService _cache;
    private readonly IStockService      _stocks;
    private readonly IAuthService       _auth;

    public PortfolioOrderHistoryViewModel(
        IOrderCacheService cache,
        IStockService      stocks,
        IAuthService       auth,
        ILogger<PortfolioOrderHistoryViewModel> logger)
        : base(logger)
    {
        _cache  = cache  ?? throw new ArgumentNullException(nameof(cache));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _auth   = auth   ?? throw new ArgumentNullException(nameof(auth));

        Subscribe();
    }

    protected override IEnumerable<Order> Source => _cache.ClosedOrders;
    protected override int GetStockId(Order order) => order.StockId;
    protected override DateTime GetSortKey(Order order) => order.UpdatedAt;

    protected override ClosedOrderRow CreateRow(Order order)
    {
        var symbol = _stocks.SymbolOrDash(order.StockId);
        return new ClosedOrderRow { Order = order, Symbol = symbol };
    }

    protected override Task RefreshSourceAsync() => _cache.RefreshAsync(_auth.CurrentUserId);

    protected override void Subscribe()   => _cache.OrdersChanged += OnSourceChanged;
    protected override void Unsubscribe() => _cache.OrdersChanged -= OnSourceChanged;

    protected override string RefreshErrorMessage => "Error refreshing order history.";
    protected override string UpdateErrorMessage  => "Error updating order history.";
}
