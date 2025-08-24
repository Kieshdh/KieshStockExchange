using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public class OpenOrdersTableViewModel : BaseViewModel
{
    private readonly IMarketOrderService _marketService;
    private readonly ILogger<OpenOrdersTableViewModel> _logger;
    private readonly ISelectedStockService _stockService;
    private int _stockId { get; set; }
    private Stock _stock { get; set; }

    public int StockId { get; private set; }

    public OpenOrdersTableViewModel(
        IMarketOrderService marketService,
        ILogger<OpenOrdersTableViewModel> logger,
        ISelectedStockService stockService)
    {
        _marketService = marketService ?? throw new ArgumentNullException(
            nameof(marketService), "Market service cannot be null.");
        _logger = logger ?? throw new ArgumentNullException(
            nameof(logger), "Logger cannot be null.");
        _stockService = stockService ?? throw new ArgumentNullException(
            nameof(stockService), "ISelectedStockContext cannot be null.");
    }

    public async Task InitializeAsync()
    {
    }
}