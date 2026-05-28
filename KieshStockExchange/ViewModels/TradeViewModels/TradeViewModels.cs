using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class TradeViewModel : BaseViewModel, IDisposable
{
    #region Selected Stock variables
    public ISelectedStockService Selected => _selected;

    // One row per (Stock, Currency) listing.
    public ObservableCollection<TradingPair> TradingPairs { get; } = new();

    private TradingPair? _pickerSelection;
    public TradingPair? PickerSelection
    {
        get => _pickerSelection;
        set
        {
            if (value is null) return;
            if (value.StockId == _selected.StockId && value.Currency == _selected.Currency)
            {
                if (!ReferenceEquals(_pickerSelection, value))
                {
                    _pickerSelection = value;
                    OnPropertyChanged();
                }
                return;
            }
            _pickerSelection = value;
            OnPropertyChanged();
            _ = ApplyPickerSelectionAsync(value);
        }
    }

    private async Task ApplyPickerSelectionAsync(TradingPair pair)
    {
        try
        {
            var stock = await _market.GetStockAsync(pair.StockId);
            if (stock is null)
            {
                _logger.LogWarning("TradingPair {Symbol} #{StockId} not resolvable on the market.",
                    pair.Symbol, pair.StockId);
                return;
            }
            await _selected.Set(stock, pair.Currency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply picker selection {Symbol} - {Currency}.",
                pair.Symbol, pair.Currency);
        }
    }

    [ObservableProperty] private bool _showAll = true;

    /// <summary>Whether the currently selected stock is on the user's watchlist.</summary>
    [ObservableProperty] private bool _isSelectedWatched;
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
    private readonly IWatchlistService _watchlist;

    public TradeViewModel( ISelectedStockService selected, IMarketDataService market,
        ILogger<TradeViewModel> logger, IUserSessionService userSession,
        IOrderEditService editService, IStockService stocks, IWatchlistService watchlist,
        PlaceOrderViewModel placingVm, ModifyOrderViewModel modifyingVm,
        TransactionHistoryViewModel historyVm,
        OpenOrdersViewModel openOrdersVm, UserPositionsViewModel positionsVm,
        ChartViewModel chartVm, OrderBookViewModel orderBookVm, OrderHistoryViewModel orderHistoryVm,
        TopNavBarViewModel topNavBarVm)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _session = userSession ?? throw new ArgumentNullException(nameof(userSession));
        _editService = editService ?? throw new ArgumentNullException(nameof(editService));
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _watchlist = watchlist ?? throw new ArgumentNullException(nameof(watchlist));

        PlacingVm = placingVm;
        ModifyingVm = modifyingVm;
        TransactionVm = historyVm;
        OpenOrdersVm = openOrdersVm;
        PositionsVm = positionsVm;
        ChartVm = chartVm;
        OrderBookVm = orderBookVm;
        OrderHistoryVm = orderHistoryVm;
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        Title = "Trade";
        _selected.PropertyChanged += OnSelectedChanged;
        _editService.PropertyChanged += OnEditServiceChanged;
        _watchlist.Changed += OnWatchlistChanged;
        RefreshIsSelectedWatched();
    }

    private void OnWatchlistChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(RefreshIsSelectedWatched);

    private void RefreshIsSelectedWatched()
    {
        var sid = _selected.StockId;
        IsSelectedWatched = sid is int id && _watchlist.IsWatched(id);
    }

    [RelayCommand]
    private async Task ToggleSelectedWatchAsync()
    {
        var sid = _selected.StockId;
        if (sid is null) return;
        try { await _watchlist.ToggleAsync(sid.Value).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Toggle watchlist from TradePage failed."); }
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
            await LoadTradingPairsAsync();

            var stock = await _market.GetStockAsync(stockId)
                ?? throw new ArgumentException($"Stock with ID {stockId} not found.");
            await _selected.Set(stock);

            _pickerSelection = TradingPairs.FirstOrDefault(p =>
                p.StockId == _selected.StockId && p.Currency == _selected.Currency);
            OnPropertyChanged(nameof(PickerSelection));

            await OpenOrdersVm.RefreshAsync();
            await TransactionVm.RefreshAsync();
            await PositionsVm.RefreshAsync();
            await OrderHistoryVm.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing TradeViewModel for stock ID {StockId}", stockId);
            throw;
        }
        finally { IsBusy = false; }
    }

    public void Cleanup() => _ = _selected.Reset();

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _selected.PropertyChanged -= OnSelectedChanged;
        _editService.PropertyChanged -= OnEditServiceChanged;
        _watchlist.Changed -= OnWatchlistChanged;
        // Every child VM subscribes to long-lived singletons (selected stock
        // service, order cache, market data, ...). Without explicit cascade
        // each Trade-page visit accumulated handlers on those singletons.
        PlacingVm.Dispose();
        ModifyingVm.Dispose();
        TransactionVm.Dispose();
        OpenOrdersVm.Dispose();
        OrderHistoryVm.Dispose();
        PositionsVm.Dispose();
        ChartVm.Dispose();
        OrderBookVm.Dispose();
        TopNavBarVm.Dispose();
        GC.SuppressFinalize(this);
    }
    #endregion

    #region Event Handlers and Commands
    private void OnSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ISelectedStockService.StockId)
                                  or nameof(ISelectedStockService.Currency)))
            return;

        var isStockChange = e.PropertyName == nameof(ISelectedStockService.StockId);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            var stockId = _selected.StockId;
            if (stockId is null)
            {
                _pickerSelection = null;
                OnPropertyChanged(nameof(PickerSelection));
                return;
            }

            var match = TradingPairs.FirstOrDefault(p =>
                p.StockId == stockId.Value && p.Currency == _selected.Currency);
            if (match is null)
            {
                // Set() assigns StockId before Currency, so fire on a real listing only —
                // synthesizing the intermediate (new stock, stale currency) pollutes the picker.
                var stock = _selected.SelectedStock;
                if (stock is not null && _stocks.IsListedIn(stock.StockId, _selected.Currency))
                {
                    match = new TradingPair(stock.StockId, stock.Symbol, _selected.Currency);
                    TradingPairs.Add(match);
                }
            }
            _pickerSelection = match;
            OnPropertyChanged(nameof(PickerSelection));

            if (isStockChange)
            {
                ChartVm.IsYAutoFit = true;
                RefreshIsSelectedWatched();
            }
        });
    }

    [RelayCommand] private async Task LoadTradingPairsAsync()
    {
        IsBusy = true;
        try
        {
            var stocks = await _market.GetAllStocksAsync();
            TradingPairs.Clear();
            foreach (var stock in stocks.OrderBy(s => s.StockId))
            {
                foreach (var listing in _stocks.GetListings(stock.StockId)
                    .OrderBy(l => l.IsPrimary ? 0 : 1)
                    .ThenBy(l => l.CurrencyType.ToString()))
                {
                    TradingPairs.Add(new TradingPair(stock.StockId, stock.Symbol, listing.CurrencyType));
                }
            }
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

/// <summary> One row in the Trade page's stock picker: a (Stock, Currency) listing. </summary>
public sealed record TradingPair(int StockId, string Symbol, CurrencyType Currency)
{
    public string Display => $"{Symbol} - {Currency}";
}
