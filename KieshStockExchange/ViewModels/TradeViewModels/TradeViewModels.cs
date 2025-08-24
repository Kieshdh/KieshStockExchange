using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;


public partial class TradeViewModel : BaseViewModel
{

    #region Services
    private readonly ISelectedStockService _stockService;
    private readonly IMarketOrderService _marketService;
    private readonly ILogger<TradeViewModel> _logger;
    private readonly IUserSessionService _session;

    private PropertyChangedEventHandler? _stockServiceChangedHandler;
    private bool _suppressSelectionChange; // prevents double-calling when we set SelectedStock programmatically
    #endregion

    #region Selected Stock variables
    public ObservableCollection<Stock> Stocks { get; } = new();
    [ObservableProperty] private Stock? _selectedStock;

    [ObservableProperty] private string _companyName;
    [ObservableProperty] private decimal? _currentPrice;
    [ObservableProperty] private string _symbol;
    #endregion

    #region ViewModel Properties
    public PlaceOrderViewModel placingVm { get; }
    public HistoryTableViewModel historyVm { get; }
    public OpenOrdersTableViewModel openOrdersVm { get; }
    public PositionsTableViewModel positionsVm { get; }
    public ChartViewModel chartVm { get; }
    public OrderBookViewModel orderBookVm { get; }
    #endregion

    #region Constructor
    public TradeViewModel(
        ISelectedStockService stockService,
        IMarketOrderService marketService,
        ILogger<TradeViewModel> logger,
        IUserSessionService userSession,
        PlaceOrderViewModel placingVm,
        HistoryTableViewModel historyVm,
        OpenOrdersTableViewModel openOrdersVm,
        PositionsTableViewModel positionsVm,
        ChartViewModel chartVm,
        OrderBookViewModel orderBookVm)
    {
        // Initialize services
        _marketService = marketService ?? 
            throw new ArgumentNullException(nameof(marketService));
        _logger = logger ?? 
            throw new ArgumentNullException(nameof(logger));
        _stockService = stockService ??
            throw new ArgumentNullException(nameof(stockService));
        _session = userSession ??
            throw new ArgumentNullException(nameof(userSession));

        // Initialize ViewModels
        this.placingVm = placingVm;
        this.historyVm = historyVm;
        this.openOrdersVm = openOrdersVm;
        this.positionsVm = positionsVm;
        this.chartVm = chartVm;
        this.orderBookVm = orderBookVm;


        // Set default parameters
        Title = "Trade";
    }
    #endregion

    #region Initialization and Cleanup
    public async Task InitializeAsync(int stockId)
    {
        IsBusy = true;
        try
        {
            // Fill the Stocks picker with the available stocks
            await LoadStocksAsync();

            // Set the initial stock
            var stock = Stocks.FirstOrDefault(s => s.StockId == stockId)
                ?? await _marketService.GetStockByIdAsync(stockId)
                ?? throw new ArgumentException($"Stock with ID {stockId} not found.");
            if (!Stocks.Any(s => s.StockId == stock.StockId))
                Stocks.Add(stock);

            // Set the selected item in the Picker without triggering a double switch
            _suppressSelectionChange = true;
            SelectedStock = stock;
            _suppressSelectionChange = false;

            // Subscribe to the service current price
            _stockServiceChangedHandler ??= (_, e) =>
            {
                if (e.PropertyName == nameof(_stockService.CurrentPrice))
                    CurrentPrice = _stockService.CurrentPrice;
                else if (e.PropertyName == nameof(_stockService.CompanyName))
                    CompanyName = _stockService.CompanyName;
                else if (e.PropertyName == nameof(_stockService.Symbol))
                    Symbol = _stockService.Symbol;
            };
            _stockService.PropertyChanged -= _stockServiceChangedHandler; // avoid double subscribe
            _stockService.PropertyChanged += _stockServiceChangedHandler;

            // Kick off background price polling at a given timeinterval
            await SwitchStockAsync(stock);

            _logger.LogInformation("TradeViewModel initialized for stock ID {StockId}", stockId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing TradeViewModel for stock ID {StockId}", stockId);
            throw; // Re-throw the exception to be handled by the caller
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cleanup()
    {
        try
        {
            _stockService.StopPriceUpdates();

            if (_stockServiceChangedHandler is not null)
                _stockService.PropertyChanged -= _stockServiceChangedHandler;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup encountered an issue, continuing.");
        }
    }

    [RelayCommand] private async Task LoadStocksAsync()
    {
        IsBusy = true;
        try
        {
            // Assuming your market service can return all stocks.
            // If you don't have this yet, add an API like GetAllStocksAsync().
            var stocks = await _marketService.GetAllStocksAsync();
            Stocks.Clear();
            foreach (var stock in stocks)
                Stocks.Add(stock);
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Switching Stocks Logic
    private async Task SwitchStockAsync(Stock? stock)
    {
        if (stock is null) return;
        IsBusy = true;
        try
        {
            // Stop previous polling (if any)
            _stockService.StopPriceUpdates();

            // Set the new stock in the service
            await _stockService.Set(stock);

            // Change binding context
            CompanyName = _stockService.CompanyName;
            Symbol = _stockService.Symbol;
            CurrentPrice = _stockService.CurrentPrice;
            Title = $"Trade - {CompanyName} ({Symbol})";

            // Start live price updates for the new stock
            _stockService.StartPriceUpdates(TimeSpan.FromSeconds(2));

            // Refresh components 
            await placingVm.InitializeAsync();
            await historyVm.InitializeAsync();
            await openOrdersVm.InitializeAsync();
            await positionsVm.InitializeAsync();
            await chartVm.InitializeAsync();
            await orderBookVm.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching to stock {StockId}", stock.StockId);
        }
        finally { IsBusy = false; }
    }

    partial void OnSelectedStockChanged(Stock? value)
    {
        if (_suppressSelectionChange) return;
        _ = SwitchStockAsync(value);
    }
    #endregion

}
