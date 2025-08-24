using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public class HistoryTableViewModel : BaseViewModel
{
    private readonly IMarketOrderService _marketService;
    private readonly ILogger<TradeViewModel> _logger;
    private readonly ISelectedStockService _stockService;

    private int _selectedStockId { get; set; }
    private Stock _stock { get; set; }

    public HistoryTableViewModel(
        IMarketOrderService marketService,
        ILogger<TradeViewModel> logger,
        ISelectedStockService stockService)
    {
        Title = "Order History";
        _marketService = marketService ?? throw new ArgumentNullException(
            nameof(marketService), "Market service cannot be null.");
        _logger = logger ?? throw new ArgumentNullException(
            nameof(logger), "Logger cannot be null.");
        _stockService = stockService ?? throw new ArgumentNullException(
            nameof(stockService), "Stock service cannot be null.");
    }

    public async Task InitializeAsync()
    {

    }


}
