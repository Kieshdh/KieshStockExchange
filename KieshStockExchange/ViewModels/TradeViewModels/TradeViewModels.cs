using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
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

    // Currency picker bound to the header. Setting it routes to
    // SelectedStockService.ChangeCurrencyAsync, which already handles
    // unsubscribing the old book + re-subscribing the new one. We suppress
    // the partial handler while we snap the picker back to the service's
    // currency (e.g. when SelectedStock changes) so the user-driven path
    // and the system-driven path don't collide.
    // 3.2 Phase B: AvailableCurrencies is filtered to the listings of the
    // currently selected stock so cross-listed stocks offer both USD and
    // EUR, EUR-only stocks offer EUR only, and so on. Rebuilt by
    // RefreshAvailableCurrencies whenever SelectedStock changes.
    public ObservableCollection<CurrencyType> AvailableCurrencies { get; } = new();

    [ObservableProperty] private CurrencyType _selectedCurrency = CurrencyType.USD;
    private bool _suppressCurrencyChange;
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
    private readonly IStockService _stocks;

    public TradeViewModel( ISelectedStockService selected, IMarketDataService market,
        ILogger<TradeViewModel> logger, IUserSessionService userSession,
        IOrderEditService editService, IStockService stocks,
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
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));

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

            // Align the active book with one of the stock's listings. If the
            // service's current currency is already a valid listing (e.g. EUR
            // for a cross-listed stock the user clicked from the EUR tab),
            // leave it alone. Otherwise fall back to the primary listing.
            if (!_stocks.IsListedIn(stock.StockId, _selected.Currency)
                && _stocks.TryGetCurrency(stock.StockId, out var primary)
                && primary != _selected.Currency)
            {
                await _selected.ChangeCurrencyAsync(primary);
            }

            SnapCurrencyPickerToService();

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

                // Snap the currency picker to the new stock's listing currency. Setting
                // SelectedCurrency before _selected.ChangeCurrencyAsync would loop back
                // through the partial handler, so suppress the round-trip — the service
                // itself updates Currency separately.
                SnapCurrencyPickerToService();
            });
        }
        else if (e.PropertyName is nameof(ISelectedStockService.Currency))
        {
            MainThread.BeginInvokeOnMainThread(SnapCurrencyPickerToService);
        }
    }

    private void SnapCurrencyPickerToService()
    {
        _suppressCurrencyChange = true;
        try
        {
            RefreshAvailableCurrencies();
            SelectedCurrency = _selected.Currency;
        }
        finally { _suppressCurrencyChange = false; }
    }

    /// <summary>
    /// Rebuild <see cref="AvailableCurrencies"/> from the listings of the
    /// currently selected stock. Cross-listed stocks expose both USD and
    /// EUR; single-listed stocks expose exactly their listing currency.
    /// </summary>
    private void RefreshAvailableCurrencies()
    {
        var stockId = _selected.SelectedStock?.StockId;
        var listings = stockId.HasValue
            ? _stocks.GetListings(stockId.Value).Select(l => l.CurrencyType).ToList()
            : new List<CurrencyType>();
        if (listings.Count == 0) listings.Add(CurrencyType.USD); // safe fallback

        AvailableCurrencies.Clear();
        foreach (var c in listings.Distinct().OrderBy(c => c.ToString()))
            AvailableCurrencies.Add(c);
    }

    partial void OnSelectedCurrencyChanged(CurrencyType value)
    {
        if (_suppressCurrencyChange) return;
        _ = _selected.ChangeCurrencyAsync(value);
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
