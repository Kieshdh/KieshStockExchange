using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class TradeViewModel : BaseViewModel, IDisposable
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
            _pickerSelection = value;   // Update the local picker selection
            _ = _selected.Set(value);   // Update the service selection
            OnPropertyChanged();        // Notify UI
        }
    }

    // Always show all orders/positions across stocks. The "Show all" toggle
    // was removed from TradePage; this stays true for the lifetime of the VM.
    [ObservableProperty] private bool _showAll = true;
    #endregion

    #region ViewModel Properties
    public PlaceOrderViewModel PlacingVm { get; }
    public ModifyOrderViewModel ModifyingVm { get; }
    public TransactionHistoryViewModel TransactionVm { get; }
    public OpenOrdersViewModel OpenOrdersVm { get; }
    public OrderHistoryViewModel OrderHistoryVm { get; }
    public UserPositionsViewModel PositionsVm { get; }
    public ChartViewModel ChartVm { get; }
    public OrderBookViewModel OrderBookVm { get; }
    public TopNavBarViewModel TopNavBarVm { get; }

    /// <summary>True while the right-hand panel is showing ModifyOrderView.</summary>
    public bool IsModifying => _editService.IsEditing;
    /// <summary>True while the right-hand panel is showing PlaceOrderView.</summary>
    public bool IsPlacing => !_editService.IsEditing;
    #endregion

    #region Fields and Constructor
    private readonly ISelectedStockService _selected;
    private readonly IMarketDataService _market;
    private readonly ILogger<TradeViewModel> _logger;
    private readonly IUserSessionService _session;
    private readonly IOrderEditService _editService;

    public TradeViewModel( ISelectedStockService selected, IMarketDataService market,
        ILogger<TradeViewModel> logger, IUserSessionService userSession,
        IOrderEditService editService,
        PlaceOrderViewModel placingVm, ModifyOrderViewModel modifyingVm,
        TransactionHistoryViewModel historyVm,
        OpenOrdersViewModel openOrdersVm, UserPositionsViewModel positionsVm,
        ChartViewModel chartVm, OrderBookViewModel orderBookVm, OrderHistoryViewModel orderHistoryVm,
        TopNavBarViewModel topNavBarVm)
    {
        // Initialize services
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _session = userSession ?? throw new ArgumentNullException(nameof(userSession));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));

        // Initialize ViewModels
        PlacingVm = placingVm;
        ModifyingVm = modifyingVm;
        TransactionVm = historyVm;
        OpenOrdersVm = openOrdersVm;
        PositionsVm = positionsVm;
        ChartVm = chartVm;
        OrderBookVm = orderBookVm;
        OrderHistoryVm = orderHistoryVm;
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        // Other initialization
        Title = "Trade";
        _selected.PropertyChanged += OnSelectedChanged;
        _editService.PropertyChanged += OnEditServiceChanged;
    }

    private void OnEditServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IOrderEditService.IsEditing)) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(IsModifying));
            OnPropertyChanged(nameof(IsPlacing));
        });
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

            // Refresh child ViewModels
            await OpenOrdersVm.RefreshAsync();
            await TransactionVm.RefreshAsync();
            await PositionsVm.RefreshAsync();
            await OrderHistoryVm.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing TradeViewModel for stock ID {StockId}", stockId);
            throw; // Re-throw the exception to be handled by the caller
        }
        finally { IsBusy = false; }
    }

    public void Cleanup() => _ = _selected.Reset();

    public void Dispose()
    {
        _selected.PropertyChanged -= OnSelectedChanged;
        _editService.PropertyChanged -= OnEditServiceChanged;
        ModifyingVm.Dispose();
        TopNavBarVm.Dispose();
    }
    #endregion

    #region Event Handlers and Commands
    private void OnSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISelectedStockService.StockId))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var stock = _selected.SelectedStock;  // Service's new selection
                if (stock is null) // No selection, should not happen
                {
                    _pickerSelection = null;
                    OnPropertyChanged(nameof(PickerSelection));
                    return;
                }

                // Ensure the Picker selection points to the instance inside Stocks collection
                var match = Stocks.FirstOrDefault(s => s.StockId == stock.StockId);
                if (match is null)
                {
                    Stocks.Add(stock);
                    match = stock;
                }

                _pickerSelection = match;  
                OnPropertyChanged(nameof(PickerSelection));
            });
        }
    }

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

    partial void OnShowAllChanged(bool value)
    {
        OpenOrdersVm.SetShowAll(value);
        TransactionVm.SetShowAll(value);
        OrderHistoryVm.SetShowAll(value);
        PositionsVm.SetShowAll(value);
    }
    #endregion
}
