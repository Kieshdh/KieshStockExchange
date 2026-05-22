using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.OtherServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.Services.UserServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.TradeViewModels;

public partial class UserPositionsViewModel : TradeTableViewModelBase<PositionRow>
{
    private readonly HashSet<(int StockId, CurrencyType Currency)> _subscriptions = new();

    #region Services and Constructor
    private readonly IStockService _stocks;
    private readonly IUserPortfolioService _portfolio;
    private readonly IMarketDataService _market;
    private readonly IAuthService _auth;

    public UserPositionsViewModel(ILogger<UserPositionsViewModel> logger, IAuthService auth,
        IUserPortfolioService portfolio, IStockService stocks, IMarketDataService market,
        ISelectedStockService selected, INotificationService notification)
        : base(selected, notification, logger)
    {
        _stocks    = stocks    ?? throw new ArgumentNullException(nameof(stocks));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _market    = market    ?? throw new ArgumentNullException(nameof(market));
        _auth      = auth      ?? throw new ArgumentNullException(nameof(auth));

        _portfolio.SnapshotChanged += OnPositionsChanged;
        InitializeSelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var key in _subscriptions)
            {
                var k = key;
                _ = Task.Run(async () =>
                {
                    try { await _market.Unsubscribe(k.StockId, k.Currency); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unsubscribe failed for {StockId}/{Currency}.", k.StockId, k.Currency);
                    }
                });
            }
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
        await Selected.Set(row.StockId, row.Currency);
    }
    #endregion

    #region Row Building
    protected override IEnumerable<PositionRow> BuildRows(int stockId, CurrencyType currency)
    {
        var snapshot = _portfolio.GetPositions().Where(p => p.Quantity > 0).ToList();
        var rows = new List<PositionRow>(snapshot.Count);

        if (ShowAll)
        {
            foreach (var pos in snapshot)
            {
                if (pos.StockId <= 0) continue;          // Skip invalid stocks
                if (pos.StockId == stockId) continue;    // Skip current stock; added below
                rows.Add(CreatePositionRow(pos, currency));
            }
            // Sort non-current positions by total value descending
            rows.Sort((a, b) => b.TotalValue.CompareTo(a.TotalValue));
        }

        // Always add the currently selected stock first (even if quantity == 0)
        if (stockId > 0)
        {
            var current = snapshot.FirstOrDefault(p => p.StockId == stockId)
                ?? new Position { StockId = stockId };
            rows.Insert(0, CreatePositionRow(current, currency));
        }

        return rows;
    }

    protected override void OnCurrentViewReplacing(IEnumerable<PositionRow> oldRows)
    {
        foreach (var row in oldRows)
            row.Dispose();
    }

    private PositionRow CreatePositionRow(Position pos, CurrencyType currency)
    {
        if (!_stocks.TryGetSymbol(pos.StockId, out string symbol))
            symbol = "-";

        var key = (pos.StockId, currency);
        if (_subscriptions.Add(key))
        {
            var stockId = pos.StockId;
            _ = Task.Run(async () =>
            {
                try { await _market.SubscribeAsync(stockId, currency); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SubscribeAsync failed for {StockId}/{Currency}.", stockId, currency);
                }
            });
            _ = Task.Run(async () =>
            {
                try { await _market.BuildFromHistoryAsync(stockId, currency); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "BuildFromHistoryAsync failed for {StockId}/{Currency}.", stockId, currency);
                }
            });
        }

        _market.Quotes.TryGetValue((pos.StockId, currency), out var live);

        return new PositionRow
        {
            Symbol = symbol,
            Currency = currency,
            Live = live,
            Pos = pos,
            TradeCommand = TradeCommand,
        };
    }

    private void OnPositionsChanged(object? sender, EventArgs e)
    {
        try { PostUpdateFromCache(); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating user positions."); }
    }
    #endregion
}

public sealed partial class PositionRow : ObservableObject, IDisposable
{
    #region Initialization Properties
    public required Position Pos { get; init; }
    public required string Symbol { get; init; }
    public required CurrencyType Currency { get; init; }
    // Injected by owner VM so Trade button binds directly.
    public required ICommand TradeCommand { get; init; }
    #endregion

    #region Live Data Property
    [ObservableProperty] private LiveQuote? _live;
    private bool _disposed;
    public int StockId => Pos.StockId;
    public decimal CurrentPrice => Live?.LastPrice ?? 0m;
    public decimal TotalValue => CurrentPrice > 0m ? CurrencyHelper.Notional(CurrentPrice, Pos.Quantity, Currency) : 0m;
    #endregion

    #region Formatted Properties
    public string Price => CurrentPrice <= 0m ? "-" : CurrencyHelper.Format(CurrentPrice, Currency);
    public string Qty => Pos.Quantity.ToString();
    public string Reserved => Pos.ReservedQuantity.ToString();
    public string Available => Pos.AvailableQuantity.ToString();
    public string Total => CurrencyHelper.Format(TotalValue, Currency);
    #endregion

    /// <summary>
    /// Re-fire change notifications for getters derived from <see cref="Pos"/>.
    /// Call after the portfolio service has mutated the cached Position's Quantity
    /// or ReservedQuantity so bindings (Qty, Reserved, Available, Total) refresh
    /// in place rather than the row being torn down and re-created.
    /// </summary>
    public void RefreshPositionFields()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(Qty));
        OnPropertyChanged(nameof(Reserved));
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Total));
    }

    #region Live Quote Change Handler and Disposal
    partial void OnLiveChanged(LiveQuote? oldValue, LiveQuote? newValue)
    {
        if (_disposed) return;
        if (oldValue == newValue) return;

        if (oldValue is not null)
            oldValue.PropertyChanged -= OnLivePropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnLivePropertyChanged;

        OnPropertyChanged(nameof(Price));
        OnPropertyChanged(nameof(Total));
    }

    private void OnLivePropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(LiveQuote.LastPrice)) return;

        // LiveQuote raises PropertyChanged from TickPipeline.ApplyTick, which
        // runs on a thread-pool reader thread when the book has no UI
        // subscribers. Without this marshal, our own OnPropertyChanged would
        // propagate on the bg thread and crash any UI binding on Price/Total.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed) return;
            try
            {
                OnPropertyChanged(nameof(Price));
                OnPropertyChanged(nameof(Total));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Shutdown race: WinUI peer torn down between queue and dispatch.
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Live is not null)
            Live.PropertyChanged -= OnLivePropertyChanged;
        Live = null;
        GC.SuppressFinalize(this);
    }
    #endregion
}
