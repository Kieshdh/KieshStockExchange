using KieshStockExchange.Services;
using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;


namespace KieshStockExchange.ViewModels.TradeViewModels;

public class OrderHistoryViewModel : StockAwareViewModel
{
    private readonly IUserOrderService _order;
    private readonly ILogger<OrderHistoryViewModel> _logger;

    public OrderHistoryViewModel(ISelectedStockService selected, IUserOrderService order,
        ILogger<OrderHistoryViewModel> logger) : base(selected)
    {
        _order = order ?? throw new ArgumentNullException(nameof(order));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {

        }
        base.Dispose(disposing);
    }
}
