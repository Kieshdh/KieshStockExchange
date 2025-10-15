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
    #region Selected Stock variables
    public ISelectedStockService Selected => _selected; // expose for bindings
    public ObservableCollection<Stock> Stocks { get; } = new();

    private Stock? _pickerSelection;
    public Stock? PickerSelection
    {
        get => _pickerSelection ?? _selected.SelectedStock;
        set
        {
            if (value is null || value == _selected.SelectedStock) return;
            _pickerSelection = value; // only to reflect the UI's selected item immediately
            // Fire-and-forget is fine here; the service pushes changes via INotifyPropertyChanged
            _ = _selected.Set(value);
            OnPropertyChanged(); // ensures the picker reflects the new selection
        }
    }
    #endregion

    #region ViewModel Properties
    public PlaceOrderViewModel PlacingVm { get; }
    public HistoryTableViewModel HistoryVm { get; }
    public OpenOrdersTableViewModel OpenOrdersVm { get; }
    public PositionsTableViewModel PositionsVm { get; }
    public ChartViewModel ChartVm { get; }
    public OrderBookViewModel OrderBookVm { get; }
    #endregion

    #region Fields and Constructor
    private readonly ISelectedStockService _selected;
    private readonly IMarketDataService _market;
    private readonly ILogger<TradeViewModel> _logger;
    private readonly IUserSessionService _session;

    public TradeViewModel(
        ISelectedStockService selected, IMarketDataService market,
        ILogger<TradeViewModel> logger, IUserSessionService userSession,
        PlaceOrderViewModel placingVm, HistoryTableViewModel historyVm,
        OpenOrdersTableViewModel openOrdersVm, PositionsTableViewModel positionsVm,
        ChartViewModel chartVm, OrderBookViewModel orderBookVm)
    {
        // Initialize services
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _session = userSession ?? throw new ArgumentNullException(nameof(userSession));

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
                ?? await _market.GetStockAsync(stockId)
                ?? throw new ArgumentException($"Stock with ID {stockId} not found.");
            // Ensure the stock is in the collection (Should be, but just in case)
            if (!Stocks.Any(s => s.StockId == stock.StockId))
                Stocks.Add(stock);

            // Set the selection in the service (this triggers data loading, subscriptions, etc.)
            await _selected.Set(stock); 

            _pickerSelection = stock; // reflect in the picker UI
            OnPropertyChanged(nameof(PickerSelection));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing TradeViewModel for stock ID {StockId}", stockId);
            throw; // Re-throw the exception to be handled by the caller
        }
        finally { IsBusy = false; }
    }

    public void Cleanup() => _ = _selected.Reset();

    [RelayCommand] private async Task LoadStocksAsync()
    {
        IsBusy = true;
        try
        {
            var stocks = await _market.GetAllStocksAsync();
            Stocks.Clear();
            foreach (var stock in stocks)
                Stocks.Add(stock);
        }
        finally { IsBusy = false; }
    }
    #endregion
}
