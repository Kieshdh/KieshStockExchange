using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.OtherServices;
using KieshStockExchange.Services.PortfolioServices;
using KieshStockExchange.Services.UserServices;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class UserPositionsViewModel : StockAwareViewModel
{
    #region Properties
    [ObservableProperty] private ObservableCollection<PositionRow> _currentView = new();

    private readonly HashSet<(int StockId, CurrencyType Currency)> _subscriptions = new();

    private bool ShowAll = false;

    public void SetShowAll(bool show)
    {
        if (ShowAll == show) return;
        ShowAll = show;
        UpdateFromCache();
    }
    #endregion

    #region Services and Constructor
    private readonly ILogger<UserPositionsViewModel> _logger;
    private readonly IStockService _stocks;
    private readonly IUserPortfolioService _portfolio;
    private readonly IMarketDataService _market;
    private readonly IAuthService _auth;

    public UserPositionsViewModel(ILogger<UserPositionsViewModel> logger, IAuthService auth,
        IUserPortfolioService portfolio, IStockService stocks, IMarketDataService market,
        ISelectedStockService selected, INotificationService notification) : base(selected, notification)
    {
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _market    = market    ?? throw new ArgumentNullException(nameof(market));
        _auth      = auth      ?? throw new ArgumentNullException(nameof(auth));

        // Subscribe to Position changes and price updates
        _portfolio.SnapshotChanged += OnPositionsChanged;

        // Initial load
        InitializeSelection();
    }
    #endregion

    #region Abstract Overrides
    protected override Task OnStockChangedAsync(int? stockId, CurrencyType currency, CancellationToken ct)
    {
        UpdateFromCache(stockId, currency); // Use the current selection; no nullables needed
        return Task.CompletedTask;
    }

    protected override Task OnPriceUpdatedAsync(int? stockId, CurrencyType currency,
        decimal price, DateTime? updatedAt, CancellationToken ct)
        => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Unsubscribe from _subscriptions data
            foreach (var key in _subscriptions)
                _ = _market.Unsubscribe(key.StockId, key.Currency);
            foreach (var row in CurrentView)
                row.Dispose();
            _portfolio.SnapshotChanged -= OnPositionsChanged;
        }
        base.Dispose(disposing);
    }
    #endregion

    #region Commands
    [RelayCommand] public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try 
        { 
            await _portfolio.RefreshAsync(_auth.CurrentUserId);
            UpdateFromCache();
        }
        catch (Exception ex) 
        { 
            _logger.LogError(ex, "Error refreshing user positions."); 
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] public async Task TradeAsync(PositionRow? row)
    {
        if (row is null) return;

        // Notify to trade the selected stock
        await Selected.Set(row.StockId);
        await Selected.ChangeCurrencyAsync(row.Currency);
    }
    #endregion

    #region Private Methods
    private void OnPositionsChanged(object? sender, EventArgs e)
    {
        try { MainThread.BeginInvokeOnMainThread(() => UpdateFromCache()); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating user positions."); }
    }

    private void UpdateFromCache(int? stockId = null, CurrencyType? currency = null)
    {
        // If no stock selected, clear view
        if (!Selected.HasSelectedStock)
        {
            CurrentView.Clear();
            return;
        }
        // Use selected stock if none provided
        stockId ??= Selected.StockId;
        currency ??= Selected.Currency;
        UpdateFromCache(stockId!.Value, currency.Value);
    }

    private void UpdateFromCache(int stockId, CurrencyType currency)
    {
        // Get the positions and set the current stock as first
        var snapshot = _portfolio.GetPositions().Where(p => p.Quantity > 0).ToList();
        var rows = new List<PositionRow>(capacity: snapshot.Count);

        // If showing all, add all non current positions
        if (ShowAll)
        {
            foreach (var pos in snapshot)
            {
                if (pos.StockId <= 0) continue; // Skip invalid stocks
                if (pos.StockId == stockId) continue; // Skip current stock
                rows.Add(CreatePostionRow(pos, currency));
            }

            // Sort based on total value descending
            rows.Sort((a, b) => b.TotalValue.CompareTo(a.TotalValue));
        }

        // Add current position first
        if (stockId > 0)
        {
            var current = snapshot.FirstOrDefault(p => p.StockId == stockId);
            var position = current ?? new Position { StockId = stockId };
            rows.Insert(0, CreatePostionRow(position, currency));
        }

        // Dispose old rows and update collection
        foreach (var row in CurrentView)
            row.Dispose();

        // Update the observable collection
        CurrentView = new ObservableCollection<PositionRow>(rows);
    }

    private PositionRow CreatePostionRow(Position pos, CurrencyType currency)
    {
        // Get stock symbol
        if (!_stocks.TryGetSymbol(pos.StockId, out string symbol))
            symbol = "-";
        
        var key = (pos.StockId, currency);

        // Subscribe to market data if not already subscribed
        if (_subscriptions.Add(key))
        {
            // Create/activate and build the LiveQuote
            _ = _market.SubscribeAsync(pos.StockId, currency);
            _ = _market.BuildFromHistoryAsync(pos.StockId, currency);
        }

        // Fetch the live quote (should be available after subscription)
        _market.Quotes.TryGetValue((pos.StockId, currency), out var live);

        return new PositionRow
        {
            Symbol = symbol, Currency = currency,
            Live = live, Pos = pos, 
        };
    }
    #endregion
}

public sealed partial class PositionRow : ObservableObject, IDisposable
{
    #region Initialization Properties
    public required Position Pos { get; init; }
    public required string Symbol { get; init; }
    public required CurrencyType Currency { get; init; }
    #endregion

    #region Live Data Property
    [ObservableProperty] private LiveQuote? _live;
    private bool _disposed;
    public int StockId => Pos.StockId;
    public decimal CurrentPrice => Live?.LastPrice ?? 0m;
    public decimal TotalValue => Pos.Quantity * CurrentPrice >= 0m ? Pos.Quantity * CurrentPrice : 0m;
    #endregion

    #region Formatted Properties
    public string Price => CurrentPrice <= 0m ? "-" : CurrencyHelper.Format(CurrentPrice, Currency);
    public string Qty => Pos.Quantity.ToString();
    public string Reserved => Pos.ReservedQuantity.ToString();
    public string Available => Pos.AvailableQuantity.ToString();
    public string Total => CurrencyHelper.Format(TotalValue, Currency);
    #endregion

    #region Live Quote Change Handler and Disposal
    partial void OnLiveChanged(LiveQuote? oldQuote, LiveQuote? newQuote)
    {
        if (_disposed) return; // Ignore if disposed
        if (oldQuote == newQuote) return; // No change

        // Unsubscribe from old quote and subscribe to new quote
        if (oldQuote is not null)
            oldQuote.PropertyChanged -= OnLivePropertyChanged;
        if (newQuote is not null)
            newQuote.PropertyChanged += OnLivePropertyChanged;

        // Refresh dependent properties
        OnPropertyChanged(nameof(Price));
        OnPropertyChanged(nameof(Total));
    }

    private void OnLivePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        // Only raise for price changes
        if (e.PropertyName is nameof(LiveQuote.LastPrice))
        {
            // Refresh dependent properties
            OnPropertyChanged(nameof(Price));
            OnPropertyChanged(nameof(Total));
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // Already disposed
        _disposed = true; // Mark as disposed
        if (Live is not null) // Unsubscribe from events
            Live.PropertyChanged -= OnLivePropertyChanged;
        Live = null; // Clear reference
        GC.SuppressFinalize(this); // Prevent finalization
    }
    #endregion
}


