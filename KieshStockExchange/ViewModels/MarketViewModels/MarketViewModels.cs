using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
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

    // USD / EUR / All tab strip. ShowAllCurrencies = true bypasses FilterCurrency.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsdTabActive))]
    [NotifyPropertyChangedFor(nameof(IsEurTabActive))]
    [NotifyPropertyChangedFor(nameof(IsAllTabActive))]
    private CurrencyType _filterCurrency;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsdTabActive))]
    [NotifyPropertyChangedFor(nameof(IsEurTabActive))]
    [NotifyPropertyChangedFor(nameof(IsAllTabActive))]
    private bool _showAllCurrencies;

    public bool IsUsdTabActive => !ShowAllCurrencies && FilterCurrency == CurrencyType.USD;
    public bool IsEurTabActive => !ShowAllCurrencies && FilterCurrency == CurrencyType.EUR;
    public bool IsAllTabActive => ShowAllCurrencies;

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
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<MarketViewModel> _logger;

    private readonly Dictionary<(int StockId, CurrencyType Currency), MarketRow> _byStockId = new();
    private IDispatcherTimer? _pollTimer;
    private bool _disposed;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public MarketViewModel(
        ILogger<MarketViewModel> logger,
        IMarketLookupService lookup,
        IMarketDataService market,
        ISelectedStockService selected,
        IUserSessionService session,
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
        _dispatcher = dispatcher  ?? throw new ArgumentNullException(nameof(dispatcher));
        Trending    = trending    ?? throw new ArgumentNullException(nameof(trending));
        TopNavBarVm = topNavBarVm ?? throw new ArgumentNullException(nameof(topNavBarVm));

        _filterCurrency = _session.BaseCurrency;
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
            if (ShowAllCurrencies)
            {
                foreach (var ccy in AvailableCurrencies)
                    await _market.SubscribeAllAsync(ccy, forUi: true).ConfigureAwait(false);
            }
            else
            {
                await _market.SubscribeAllAsync(FilterCurrency, forUi: true).ConfigureAwait(false);
            }

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

    /// <summary> Snapshot LiveQuotes into MarketRow. Updates in place to avoid CollectionView flicker. </summary>
    private void Poll()
    {
        IEnumerable<LiveQuote> source = _market.Quotes.Values;
        if (!ShowAllCurrencies)
            source = source.Where(q => q.Currency == FilterCurrency);
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
                var row = MarketRow.FromQuote(q);
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

    partial void OnShowAllCurrenciesChanged(bool value)
    {
        ResetRowMap();
        _ = RefreshAsync();
    }

    private void ResetRowMap()
    {
        _byStockId.Clear();
        AllStocks.Clear();
        FilteredStocks.Clear();
        PagedStocks.Clear();
        PageNumber = 0;
    }

    /// <summary> Tab strip handler. <paramref name="tag"/> is "USD", "EUR", or "All". </summary>
    [RelayCommand]
    private void SelectCurrencyTab(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        if (string.Equals(tag, "All", StringComparison.OrdinalIgnoreCase))
        {
            if (!ShowAllCurrencies) ShowAllCurrencies = true;
            return;
        }
        if (CurrencyHelper.TryFromIsoCode(tag, out var ccy))
        {
            if (ShowAllCurrencies) ShowAllCurrencies = false;
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
        Currency         = q.Currency,
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
