using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using KieshStockExchange.ViewModels.OtherViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace KieshStockExchange.ViewModels.MarketViewModels;

public partial class MarketViewModel : BaseViewModel, IDisposable
{
    #region Observable state
    public ObservableCollection<MarketRow> AllStocks { get; } = new();
    public ObservableCollection<MarketRow> FilteredStocks { get; } = new();
    /// <summary>The current visible page over <see cref="FilteredStocks"/>.</summary>
    public ObservableCollection<MarketRow> PagedStocks { get; } = new();
    /// <summary>
    /// "My Watchlist" card content — user's starred stocks in their saved order.
    /// Independent of the currency tab so the card stays useful even when the user
    /// is on USD and a watched stock is EUR.
    /// </summary>
    public ObservableCollection<MarketRow> Watchlist { get; } = new();
    public bool HasWatchlist => Watchlist.Count > 0;

    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrev))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(PageDisplay))]
    private int _pageNumber; // 0-based

    // USD / EUR / Watchlist tab strip. ShowWatchlistOnly = true filters to the
    // user's starred stocks across both currencies; FilterCurrency is ignored then.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsdTabActive))]
    [NotifyPropertyChangedFor(nameof(IsEurTabActive))]
    [NotifyPropertyChangedFor(nameof(IsWatchlistTabActive))]
    private CurrencyType _filterCurrency;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsdTabActive))]
    [NotifyPropertyChangedFor(nameof(IsEurTabActive))]
    [NotifyPropertyChangedFor(nameof(IsWatchlistTabActive))]
    private bool _showWatchlistOnly;

    public bool IsUsdTabActive => !ShowWatchlistOnly && FilterCurrency == CurrencyType.USD;
    public bool IsEurTabActive => !ShowWatchlistOnly && FilterCurrency == CurrencyType.EUR;
    public bool IsWatchlistTabActive => ShowWatchlistOnly;

    public IReadOnlyList<CurrencyType> AvailableCurrencies { get; } =
        new[] { CurrencyType.USD, CurrencyType.EUR };

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
    private readonly IUserSessionService _session;
    private readonly IWatchlistService _watchlist;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<MarketViewModel> _logger;

    private readonly Dictionary<(int StockId, CurrencyType Currency), MarketRow> _byStockId = new();

    // Watchlist card keeps its own row cache so successive RebuildWatchlistCard
    // calls update existing rows in place instead of recreating instances. The
    // SyncRows identity check would otherwise swap every row on every poll
    // (every 5s), making the card visibly flicker.
    private readonly Dictionary<int, MarketRow> _watchlistRowsById = new();
    private IDispatcherTimer? _pollTimer;
    private bool _disposed;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public MarketViewModel(
        ILogger<MarketViewModel> logger,
        IMarketLookupService lookup,
        IMarketDataService market,
        ISelectedStockService selected,
        IUserSessionService session,
        IWatchlistService watchlist,
        IDispatcher dispatcher,
        ITrendingService trending,
        TopNavBarViewModel topNavBarVm)
    {
        Title = "Market";
        _logger     = logger      ?? throw new ArgumentNullException(nameof(logger));
        _lookup     = lookup      ?? throw new ArgumentNullException(nameof(lookup));
        _market     = market      ?? throw new ArgumentNullException(nameof(market));
        _selected   = selected    ?? throw new ArgumentNullException(nameof(selected));
        _session    = session     ?? throw new ArgumentNullException(nameof(session));
        _watchlist  = watchlist   ?? throw new ArgumentNullException(nameof(watchlist));
        _dispatcher = dispatcher  ?? throw new ArgumentNullException(nameof(dispatcher));
        Trending    = trending    ?? throw new ArgumentNullException(nameof(trending));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        _filterCurrency = _session.BaseCurrency;
        _watchlist.Changed += OnWatchlistChanged;
        _market.QuoteUpdated += OnQuoteUpdated;
    }

    // Cold-start used to show an empty grid until the 5s timer ticked. By
    // hooking the live quote stream we sweep new rows into the UI as soon
    // as they arrive; debounced via _pollPending so a burst of quotes only
    // triggers one rebuild per UI frame.
    private bool _pollPending;
    private void OnQuoteUpdated(object? sender, LiveQuote quote)
    {
        if (_disposed) return;
        if (_pollPending) return;
        _pollPending = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _pollPending = false;
            if (_disposed) return;
            try { Poll(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Market live-quote poll failed."); }
        });
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
            // The "My Watchlist" card sits above the tab and shows starred
            // stocks across all currencies, so always subscribe to every
            // supported currency. The cost is a few extra group joins;
            // the benefit is the card populates on the USD tab too instead
            // of only when the Watchlist tab is selected.
            foreach (var ccy in AvailableCurrencies)
                await _market.SubscribeAllAsync(ccy, forUi: true).ConfigureAwait(false);

            // Prime trending so the panel isn't empty until the next timer tick.
            await Trending.RecomputeMoversAsync().ConfigureAwait(false);

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
            await _selected.Set(row.StockId, row.Currency).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.GoToAsync("///TradePage"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to TradePage for {Symbol}.", row.Symbol);
        }
    }

    [RelayCommand]
    private async Task ToggleWatchAsync(MarketRow? row)
    {
        if (row is null) return;
        try
        {
            await _watchlist.ToggleAsync(row.StockId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle watchlist for {Symbol}.", row.Symbol);
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

    /// <summary>Stop the 5s polling timer without tearing down state. Page
    /// disappearance calls this so the subscription, _byStockId rows, and
    /// PagedStocks survive — the next OnAppearing reads from a warm cache
    /// instead of cold-starting the subscribe + first-poll loop.</summary>
    public void PausePolling()
    {
        if (_pollTimer is null) return;
        _pollTimer.Stop();
        _pollTimer = null;
    }

    /// <summary> Snapshot LiveQuotes into MarketRow. Updates in place to avoid CollectionView flicker. </summary>
    private void Poll()
    {
        IEnumerable<LiveQuote> source = _market.Quotes.Values;
        if (ShowWatchlistOnly)
        {
            var watched = _watchlist.GetStockIds().ToHashSet();
            source = source.Where(q => watched.Contains(q.StockId));
        }
        else
        {
            source = source.Where(q => q.Currency == FilterCurrency);
        }
        var quotes = source
            .OrderBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.Currency)
            .ToList();

        bool structureChanged = false;

        foreach (var q in quotes)
        {
            var key = (q.StockId, q.Currency);
            if (_byStockId.TryGetValue(key, out var existing))
            {
                existing.UpdateFrom(q);
            }
            else
            {
                var row = MarketRow.FromQuote(q, TradeCommand, ToggleWatchCommand, _watchlist.IsWatched(q.StockId));
                _byStockId[key] = row;
                AllStocks.Add(row);
                structureChanged = true;
            }
        }

        if (_byStockId.Count != quotes.Count)
        {
            var present = quotes.Select(q => (q.StockId, q.Currency)).ToHashSet();
            var stale = _byStockId.Keys.Where(k => !present.Contains(k)).ToList();
            foreach (var key in stale)
            {
                if (_byStockId.Remove(key, out var row))
                {
                    AllStocks.Remove(row);
                    structureChanged = true;
                }
            }
        }

        if (structureChanged)
        {
            ApplyFilter();
            RebuildPagedStocks();
        }

        // Keep the dedicated Watchlist card in sync — its live values come from the
        // same LiveQuote cache, and rebuilding here picks up any rows whose quote
        // arrived after the last membership change.
        RebuildWatchlistCard();
    }
    #endregion

    #region Filtering
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        PageNumber = 0; // reset to first page on filter change
        RebuildPagedStocks();
    }

    // Tab swap: drop the row map so the new currency's books reflow on next Poll.
    partial void OnFilterCurrencyChanged(CurrencyType value)
    {
        ResetRowMap();
        _ = RefreshAsync();
    }

    partial void OnShowWatchlistOnlyChanged(bool value)
    {
        ResetRowMap();
        _ = RefreshAsync();
    }

    private void OnWatchlistChanged(object? sender, EventArgs e)
    {
        // Watchlist membership flipped. Sync IsWatched on every existing row,
        // rebuild the dedicated Watchlist card, and re-derive paging on the
        // Watchlist tab where structure can change.
        _ = MainThread.InvokeOnMainThreadAsync(() =>
        {
            var watched = _watchlist.GetStockIds().ToHashSet();
            foreach (var row in AllStocks)
                row.IsWatched = watched.Contains(row.StockId);

            RebuildWatchlistCard();

            if (ShowWatchlistOnly)
            {
                Poll();
                ApplyFilter();
                RebuildPagedStocks();
            }
        });
    }

    /// <summary>
    /// Rebuild the "My Watchlist" top card in saved sort order. Pulls live data
    /// from the LiveQuote cache; rows whose quote hasn't loaded yet are skipped
    /// and picked up on the next Poll() that includes them.
    /// </summary>
    private void RebuildWatchlistCard()
    {
        var ids = _watchlist.GetStockIds();
        var desired = new List<MarketRow>(ids.Count);
        var seen = new HashSet<int>(ids.Count);

        foreach (var stockId in ids)
        {
            // Prefer a USD quote, else any currency this stock trades in.
            var quote = _market.Quotes.Values
                .Where(q => q.StockId == stockId)
                .OrderBy(q => q.Currency != CurrencyType.USD)
                .FirstOrDefault();
            if (quote is null) continue;

            // Reuse the cached row instance so SyncRows treats it as identity
            // match and skips the swap; only the bound property values change.
            if (!_watchlistRowsById.TryGetValue(stockId, out var row))
            {
                row = MarketRow.FromQuote(quote, TradeCommand, ToggleWatchCommand, isWatched: true);
                _watchlistRowsById[stockId] = row;
            }
            else
            {
                row.UpdateFrom(quote);
                if (!row.IsWatched) row.IsWatched = true;
            }
            desired.Add(row);
            seen.Add(stockId);
        }

        // Drop cached rows that left the watchlist so the dictionary doesn't grow.
        var stale = _watchlistRowsById.Keys.Where(id => !seen.Contains(id)).ToList();
        foreach (var id in stale) _watchlistRowsById.Remove(id);

        SyncRows(Watchlist, desired);
        OnPropertyChanged(nameof(HasWatchlist));
    }

    [RelayCommand]
    private async Task MoveWatchUpAsync(MarketRow? row)
    {
        if (row is null) return;
        var ids = _watchlist.GetStockIds().ToList();
        var i = ids.IndexOf(row.StockId);
        if (i <= 0) return;
        (ids[i - 1], ids[i]) = (ids[i], ids[i - 1]);
        try { await _watchlist.ReorderAsync(ids).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Move watchlist up failed."); }
    }

    [RelayCommand]
    private async Task MoveWatchDownAsync(MarketRow? row)
    {
        if (row is null) return;
        var ids = _watchlist.GetStockIds().ToList();
        var i = ids.IndexOf(row.StockId);
        if (i < 0 || i >= ids.Count - 1) return;
        (ids[i], ids[i + 1]) = (ids[i + 1], ids[i]);
        try { await _watchlist.ReorderAsync(ids).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Move watchlist down failed."); }
    }

    private void ResetRowMap()
    {
        _byStockId.Clear();
        AllStocks.Clear();
        FilteredStocks.Clear();
        PagedStocks.Clear();
        PageNumber = 0;
    }

    /// <summary> Tab strip handler. <paramref name="tag"/> is "USD", "EUR", or "Watchlist". </summary>
    [RelayCommand]
    private void SelectCurrencyTab(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (string.Equals(tag, "Watchlist", StringComparison.OrdinalIgnoreCase))
        {
            if (!ShowWatchlistOnly) ShowWatchlistOnly = true;
            return;
        }
        if (CurrencyHelper.TryFromIsoCode(tag, out var ccy))
        {
            if (ShowWatchlistOnly) ShowWatchlistOnly = false;
            if (FilterCurrency != ccy) FilterCurrency = ccy;
        }
    }

    private void ApplyFilter()
    {
        // Sync in place — Clear+Add would force CollectionView Reset and flash.
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

    /// <summary> Slice FilteredStocks into PagedStocks for the current page. </summary>
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

    /// <summary> Sync target to desired with Move/Insert/Remove, no Reset. </summary>
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
        _watchlist.Changed -= OnWatchlistChanged;
        _market.QuoteUpdated -= OnQuoteUpdated;
        TopNavBarVm.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary> Row bound by the All-Stocks table. Identity is (StockId, Currency). </summary>
public partial class MarketRow : ObservableObject
{
    public required int StockId { get; init; }
    public required string Symbol { get; init; }
    public required string CompanyName { get; init; }
    public required CurrencyType Currency { get; init; }
    // Injected by owner VM so Trade button binds directly.
    public required ICommand TradeCommand { get; init; }
    public required ICommand ToggleWatchCommand { get; init; }

    [ObservableProperty] private string _lastPriceDisplay = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBullish))]
    [NotifyPropertyChangedFor(nameof(IsBearish))]
    private decimal _changePct;

    [ObservableProperty] private string _changePctDisplay = "-";

    [ObservableProperty] private string _dollarVolumeDisplay = "-";

    [ObservableProperty] private bool _isWatched;

    public bool IsBullish => ChangePct > 0m;
    public bool IsBearish => ChangePct < 0m;

    public static MarketRow FromQuote(LiveQuote q, ICommand tradeCommand, ICommand toggleWatchCommand, bool isWatched) => new()
    {
        StockId             = q.StockId,
        Symbol              = q.Symbol,
        CompanyName         = q.CompanyName,
        Currency            = q.Currency,
        LastPriceDisplay    = q.LastPriceDisplay,
        ChangePct           = q.ChangePct,
        ChangePctDisplay    = q.ChangePctDisplay,
        DollarVolumeDisplay = q.DollarVolumeDisplay,
        TradeCommand        = tradeCommand,
        ToggleWatchCommand  = toggleWatchCommand,
        IsWatched           = isWatched,
    };

    public void UpdateFrom(LiveQuote q)
    {
        LastPriceDisplay     = q.LastPriceDisplay;
        ChangePct            = q.ChangePct;
        ChangePctDisplay     = q.ChangePctDisplay;
        DollarVolumeDisplay  = q.DollarVolumeDisplay;
    }
}
