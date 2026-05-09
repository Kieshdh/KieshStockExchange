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
    /// <summary>The current visible page over <see cref="FilteredStocks"/>.</summary>
    public ObservableCollection<MarketRow> PagedStocks { get; } = new();

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    private int _pageNumber; // 0-based

    public int PageSize { get; set; } = 20;
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(FilteredStocks.Count / (double)PageSize));
    public bool CanGoPrev => PageNumber > 0;
    public bool CanGoNext => PageNumber < TotalPages - 1;
    public string PageDisplay => $"{PageNumber + 1} / {TotalPages}";

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
            // Idempotent — already-subscribed books just bump the ref count.
            await _market.SubscribeAllAsync(CurrencyType.USD, forUi: true).ConfigureAwait(false);

            // Prime the trending lists immediately. Without this the user sees
            // an empty Top Gainers / Top Losers / Most Active panel for up to
            // 5s while waiting for TrendingService's periodic timer.
            await Trending.RecomputeMoversAsync().ConfigureAwait(false);

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
        {
            ApplyFilter();
            RebuildPagedStocks();
        }
    }
    #endregion

    #region Filtering
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        PageNumber = 0; // reset to first page on filter change
        RebuildPagedStocks();
    }

    private void ApplyFilter()
    {
        // Build the desired filtered list as a plain List, then sync it into
        // FilteredStocks in place. Clear-and-Add raised a Reset event each
        // time which forced the bound CollectionView to tear down all rows
        // (visible flash on every refresh).
        var desired = new List<MarketRow>(AllStocks.Count);
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var row in AllStocks) desired.Add(row);
        }
        else
        {
            var q = SearchText.Trim();
            foreach (var row in AllStocks)
            {
                if (row.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || row.CompanyName.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    desired.Add(row);
                }
            }
        }
        SyncRows(FilteredStocks, desired);
    }
    #endregion

    #region Pagination
    [RelayCommand(CanExecute = nameof(CanGoPrev))]
    private void PrevPage()
    {
        if (!CanGoPrev) return;
        PageNumber--;
        RebuildPagedStocks();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        if (!CanGoNext) return;
        PageNumber++;
        RebuildPagedStocks();
    }

    /// <summary>
    /// Rebuilds <see cref="PagedStocks"/> with the slice of <see cref="FilteredStocks"/>
    /// for the current page. Clamps <see cref="PageNumber"/> if the filter shrank the set.
    /// Sync is in place to avoid the Clear+Add Reset event that would otherwise
    /// flash the bound All Stocks table on every refresh.
    /// </summary>
    private void RebuildPagedStocks()
    {
        var totalPages = TotalPages;
        if (PageNumber >= totalPages) PageNumber = totalPages - 1;
        if (PageNumber < 0) PageNumber = 0;

        var start = PageNumber * PageSize;
        var end = Math.Min(start + PageSize, FilteredStocks.Count);
        var desired = new List<MarketRow>(end - start);
        for (int i = start; i < end; i++) desired.Add(FilteredStocks[i]);
        SyncRows(PagedStocks, desired);

        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageDisplay));
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Sync <paramref name="target"/> to match <paramref name="desired"/> using
    /// only Move / Insert / Add / Remove events. CollectionView keeps existing
    /// row visuals where possible; bindings stay attached to their reused rows
    /// so values never go stale.
    /// </summary>
    private static void SyncRows(ObservableCollection<MarketRow> target, IList<MarketRow> desired)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            if (i >= target.Count) { target.Add(desired[i]); continue; }
            if (ReferenceEquals(target[i], desired[i])) continue;

            int existing = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i])) { existing = j; break; }
            }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
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
