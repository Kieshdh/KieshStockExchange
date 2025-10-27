using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public class HistoryViewModel : BaseViewModel
{
    private readonly IMarketOrderService _marketService;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly ISelectedStockService _stockService;

    private int _selectedStockId { get; set; }
    private Stock _stock { get; set; }

    public HistoryViewModel( IMarketOrderService marketService,
        ILogger<HistoryViewModel> logger, ISelectedStockService stockService)
    {
        Title = "Order History";
        _marketService = marketService ?? throw new ArgumentNullException(nameof(marketService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stockService = stockService ?? throw new ArgumentNullException(nameof(stockService));
    }

    public async Task InitializeAsync()
    {

    }


}
