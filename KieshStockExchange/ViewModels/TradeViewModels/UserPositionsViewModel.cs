using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public class UserPositionsViewModel : BaseViewModel
{
    private readonly IMarketOrderService _marketService;
    private readonly ILogger<UserPositionsViewModel> _logger;
    private readonly ISelectedStockService _selected;
    private int StockId  => _selected.StockId ?? 0;
    private Stock? _stock => _selected.SelectedStock;

    public UserPositionsViewModel(IMarketOrderService marketService,
        ILogger<UserPositionsViewModel> logger, ISelectedStockService stockService)
    {
        _marketService = marketService ?? throw new ArgumentNullException(
            nameof(marketService), "Market service cannot be null.");
        _logger = logger ?? throw new ArgumentNullException(
            nameof(logger), "Logger cannot be null.");
        _selected = stockService ?? throw new ArgumentNullException(
            nameof(stockService), "ISelectedStockService cannot be null.");
    }

    public async Task InitializeAsync()
    {
    }
}
