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
    public PlaceOrderViewModel PlacingVm { get; }
    public HistoryTableViewModel HistoryVm { get; }
    public OpenOrdersTableViewModel OpenOrdersVm { get; }
    public PositionsTableViewModel PositionsVm { get; }
    public ChartViewModel ChartVm { get; }
    public OrderBookViewModel OrderBookVm { get; }
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
        PlacingVm = placingVm;
        HistoryVm = historyVm;
        OpenOrdersVm = openOrdersVm;
        PositionsVm = positionsVm;
        ChartVm = chartVm;
        OrderBookVm = orderBookVm;

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
            _logger.LogInformation("TradeViewModel initializing for stock #{StockId}", stockId);
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
                var name = e.PropertyName;

                // Handle single-property updates and "refresh-all" notifications
                if (string.IsNullOrEmpty(name) ||
                    name == nameof(ISelectedStockService.CurrentPrice) ||
                    name == nameof(ISelectedStockService.CurrentPriceDisplay) ||
                    name == nameof(ISelectedStockService.PriceUpdatedAt) ||
                    name == nameof(_stockService.CompanyName) ||
                    name == nameof(_stockService.Symbol))
                {
                    ApplyStockServiceSnapshot();
                }
            };
            _stockService.PropertyChanged -= _stockServiceChangedHandler; // avoid double subscribe
            _stockService.PropertyChanged += _stockServiceChangedHandler;

            // Kick off background price polling at a given timeinterval
            await SwitchStockAsync(stock);
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
            var stocks = await _marketService.GetAllStocksAsync();
            Stocks.Clear();
            foreach (var stock in stocks)
                Stocks.Add(stock);
        }
        finally { IsBusy = false; }
    }
    #endregion

    #region Switching Stocks Logic
    private async Task SwitchStockAsync(Stock stock)
    {
        if (stock is null) return;
        IsBusy = true;
        try
        {
            // Stop previous polling (if any)
            _stockService.StopPriceUpdates();

            // Set the new stock in the service
            await _stockService.Set(stock);

            // Apply the new snapshot (thread-safe)
            ApplyStockServiceSnapshot();

            // Start live price updates for the new stock
            _stockService.StartPriceUpdates(TimeSpan.FromSeconds(2));

            // Refresh components 
            await PlacingVm.InitializeAsync();
            await HistoryVm.InitializeAsync();
            await OpenOrdersVm.InitializeAsync();
            await PositionsVm.InitializeAsync();
            await ChartVm.InitializeAsync();
            await OrderBookVm.InitializeAsync();
            _logger.LogInformation("Succesfully switched stocks to {StocksId}", stock.StockId);
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

    #region Helpers
    // Apply current values from _stockService to bindable VM properties on the UI thread
    private void ApplyStockServiceSnapshot()
    {
        void apply()
        {
            CompanyName = _stockService.CompanyName;
            Symbol = _stockService.Symbol;
            CurrentPrice = _stockService.CurrentPrice;
            Title = $"Trade - {CompanyName} ({Symbol})";
        }

        if (MainThread.IsMainThread) apply();
        else MainThread.BeginInvokeOnMainThread(apply);
    }
    #endregion
}
