using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.ViewModels.MarketViewModels;

public partial class MarketViewModel : BaseViewModel, IDisposable
{
    #region Observable state
    public ObservableCollection<MarketRow> AllStocks { get; } = new();
    public ObservableCollection<MarketRow> FilteredStocks { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    public ITrendingService Trending { get; }
    public TopNavBarViewModel TopNavBarVm { get; }
    #endregion

    #region Services and constructor
    private readonly IMarketLookupService _lookup;
    private readonly IMarketDataService _market;
    private readonly ISelectedStockService _selected;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<MarketViewModel> _logger;

    private readonly Dictionary<int, MarketRow> _byStockId = new();
    private IDispatcherTimer? _pollTimer;
    private bool _disposed;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public MarketViewModel(
        ILogger<MarketViewModel> logger,
        IMarketLookupService lookup,
        IMarketDataService market,
        ISelectedStockService selected,
        IDispatcher dispatcher,
        ITrendingService trending,
        TopNavBarViewModel topNavBarVm)
    {
        Title = "Market";
        _logger     = logger      ?? throw new ArgumentNullException(nameof(logger));
        _lookup     = lookup      ?? throw new ArgumentNullException(nameof(lookup));
        _market     = market      ?? throw new ArgumentNullException(nameof(market));
        _selected   = selected    ?? throw new ArgumentNullException(nameof(selected));
        _dispatcher = dispatcher  ?? throw new ArgumentNullException(nameof(dispatcher));
        Trending    = trending    ?? throw new ArgumentNullException(nameof(trending));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));
    }
    #endregion

    #region Commands
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // Idempotent Ã¢â‚¬â€ already-subscribed books just bump the ref count.
            await _market.SubscribeAllAsync(CurrencyType.USD, forUi: true).ConfigureAwait(false);

            // Force an immediate poll then start the 5-second cadence so the
            // table rows refresh on a budget instead of on every tick.
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Poll();
                EnsurePollTimer();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh market list.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TradeAsync(MarketRow? row)
    {
        if (row is null) return;
        try
        {
            await _selected.Set(row.StockId).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.GoToAsync("///TradePage"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to TradePage for {Symbol}.", row.Symbol);
        }
    }
    #endregion

    #region Polling
    private void EnsurePollTimer()
    {
        if (_pollTimer != null) return;
        _pollTimer = _dispatcher.CreateTimer();
        _pollTimer.Interval = PollInterval;
        _pollTimer.Tick += (s, e) => Poll();
        _pollTimer.Start();
    }

    /// <summary>
    /// Snapshot the current LiveQuote state into MarketRow instances. Existing
    /// rows get updated in place via observable properties so the CollectionView
    /// only re-paints the changed cells. We only touch the AllStocks /
    /// FilteredStocks collections when a stock is actually added or removed,
    /// otherwise the CollectionView would tear down and rebuild every row each
    /// poll (visible as a brief text flicker).
    /// </summary>
    private void Poll()
    {
        var quotes = _market.Quotes.Values
            .Where(q => q.Currency == CurrencyType.USD)
            .OrderBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool structureChanged = false;

        // Update existing rows in place; add rows for new stocks.
        foreach (var q in quotes)
        {
            if (_byStockId.TryGetValue(q.StockId, out var existing))
            {
                existing.UpdateFrom(q);
            }
            else
            {
                var row = MarketRow.FromQuote(q);
                _byStockId[q.StockId] = row;
                AllStocks.Add(row);
                structureChanged = true;
            }
        }

        // Drop rows whose stock disappeared.
        if (_byStockId.Count != quotes.Count)
        {
            var present = quotes.Select(q => q.StockId).ToHashSet();
            var stale = _byStockId.Keys.Where(id => !present.Contains(id)).ToList();
            foreach (var id in stale)
            {
                if (_byStockId.Remove(id, out var row))
                {
                    AllStocks.Remove(row);
                    structureChanged = true;
                }
            }
        }

        // Only rebuild FilteredStocks when the underlying set actually changed
        // (or when the search text changes - that path is in OnSearchTextChanged).
        if (structureChanged)
            ApplyFilter();
    }
    #endregion

    #region Filtering
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredStocks.Clear();
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var row in AllStocks) FilteredStocks.Add(row);
            return;
        }

        var q = SearchText.Trim();
        foreach (var row in AllStocks)
        {
            if (row.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase)
                || row.CompanyName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredStocks.Add(row);
            }
        }
    }
    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer = null;
        }
        TopNavBarVm.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Snapshot row bound by the All-Stocks table. Refreshed in place every poll
/// tick so the collection identity stays stable and the CollectionView
/// preserves scroll/selection between updates.
/// </summary>
public partial class MarketRow : ObservableObject
{
    public required int StockId { get; init; }
    public required string Symbol { get; init; }
    public required string CompanyName { get; init; }

    [ObservableProperty] private string _lastPriceDisplay = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBullish))]
    [NotifyPropertyChangedFor(nameof(IsBearish))]
    private decimal _changePct;

    [ObservableProperty] private string _changePctDisplay = "-";

    [ObservableProperty] private string _dollarVolumeDisplay = "-";

    public bool IsBullish => ChangePct > 0m;
    public bool IsBearish => ChangePct < 0m;

    public static MarketRow FromQuote(LiveQuote q) => new()
    {
        StockId          = q.StockId,
        Symbol           = q.Symbol,
        CompanyName      = q.CompanyName,
        LastPriceDisplay = q.LastPriceDisplay,
        ChangePct        = q.ChangePct,
        ChangePctDisplay = q.ChangePctDisplay,
        DollarVolumeDisplay = q.DollarVolumeDisplay,
    };

    public void UpdateFrom(LiveQuote q)
    {
        LastPriceDisplay     = q.LastPriceDisplay;
        ChangePct            = q.ChangePct;
        ChangePctDisplay     = q.ChangePctDisplay;
        DollarVolumeDisplay  = q.DollarVolumeDisplay;
    }
}
