using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class TrendingService : ObservableObject, ITrendingService, IDisposable
{
    #region Movers Lists
    // Bound observable collections — keyed by Symbol via _rowsBySymbol so the
    // CollectionView can keep its row visuals across recomputes and only sees
    // Move / Insert / Remove events (never Replace or Reset).
    private readonly ObservableCollection<MoverRow> _topGainers = new();
    private readonly ObservableCollection<MoverRow> _topLosers = new();
    private readonly ObservableCollection<MoverRow> _mostActive = new();

    private readonly ReadOnlyObservableCollection<MoverRow> _topGainersView;
    private readonly ReadOnlyObservableCollection<MoverRow> _topLosersView;
    private readonly ReadOnlyObservableCollection<MoverRow> _mostActiveView;

    public IReadOnlyList<MoverRow> TopGainers => _topGainersView;
    public IReadOnlyList<MoverRow> TopLosers => _topLosersView;
    public IReadOnlyList<MoverRow> MostActive => _mostActiveView;

    // Per-list symbol → row map. Used to reuse the same MoverRow instance
    // across recomputes when the same symbol is still in the list, which keeps
    // its CollectionView visual stable (no flicker).
    private readonly Dictionary<string, MoverRow> _gainerRowsBySymbol  = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MoverRow> _loserRowsBySymbol   = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MoverRow> _activeRowsBySymbol  = new(StringComparer.Ordinal);
    #endregion

    #region Fields and Constructor
    private readonly IDispatcher _dispatcher; // to marshal changes to UI thread
    private readonly ILogger<TrendingService> _logger;
    private readonly IDataBaseService _db;
    private readonly IMarketDataService _market;

    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(SecondsBetweenUpdates));
    private readonly CancellationTokenSource _cts = new();

    private const int MaxMovers = 3;
    private const int SecondsBetweenUpdates = 5;

    public CurrencyType Currency { get; set; } = CurrencyType.USD;

    public TrendingService(IDispatcher dispatcher, ILogger<TrendingService> logger,
        IDataBaseService db, IMarketDataService market)
    {
        // Dependencies
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _market = market ?? throw new ArgumentNullException(nameof(market));

        // Read-only wrappers for external consumption
        _topGainersView = new ReadOnlyObservableCollection<MoverRow>(_topGainers);
        _topLosersView = new ReadOnlyObservableCollection<MoverRow>(_topLosers);
        _mostActiveView = new ReadOnlyObservableCollection<MoverRow>(_mostActive);

        // Top movers refresh every 5s — that's enough for a sidebar.
        // Intentionally NOT subscribing to QuoteUpdated (would cause UI-thread storms on
        // every settlement) and NOT calling SubscribeAllAsync (forces ring/candle/timer
        // machinery for every stock even when no UI is watching).
        _ = RunLoopAsync(_cts.Token);
    }
    #endregion

    #region Core
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (await _timer.WaitForNextTickAsync(ct))
            await RecomputeMoversAsync();
    }

    public Task RecomputeMoversAsync()
    {
        // Snapshot LiveQuote fields into immutable records on the timer thread
        // BEFORE handing them to the dispatcher. TickPipeline may be writing
        // LiveQuote.LastPriceDisplay et al. concurrently on a different bg
        // thread; capturing live references in the dispatch lambda would
        // produce wrong-thread PropertyChanged propagation through MoverRow's
        // setters once the UI thread reads them. Records are by-value frozen.
        var snap = _market.Quotes.Values.ToList();

        var gainers = snap.Where(q => q.Open > 0m && q.ChangePct > 0m)
            .OrderByDescending(q => q.ChangePct).Take(MaxMovers)
            .Select(ToSnapshot).ToList();

        var losers = snap.Where(q => q.Open > 0m && q.ChangePct < 0m)
            .OrderBy(q => q.ChangePct).Take(MaxMovers)
            .Select(ToSnapshot).ToList();

        var actives = snap.Where(q => q.Volume > 0)
            .OrderByDescending(q => q.Volume).Take(MaxMovers)
            .Select(ToSnapshot).ToList();

        // MainThread is MAUI's canonical UI-thread marshal; the injected
        // IDispatcher has been observed to land in the wrong COM apartment
        // on WinUI, causing RPC_E_WRONG_THREAD when the CollectionChanged
        // event is delivered to a CollectionView subscriber.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                SyncList(_topGainers, gainers, _gainerRowsBySymbol);
                SyncList(_topLosers, losers, _loserRowsBySymbol);
                SyncList(_mostActive, actives, _activeRowsBySymbol);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrendingService: SyncList threw during dispatched update.");
            }
        });

        return Task.CompletedTask;
    }

    private static MoverSnapshot ToSnapshot(LiveQuote q) =>
        new(q.Symbol, q.LastPriceDisplay, q.ChangePctDisplay, q.ChangePct);

    /// <summary>
    /// Project the ranked snapshot list into <paramref name="target"/>:
    ///   - Reuse a MoverRow instance if its symbol is still present (calling
    ///     UpdateFrom so the row's bound cells refresh in place).
    ///   - Move existing rows into their new ranking position.
    ///   - Insert new entrants and remove dropouts as individual events.
    /// CollectionView reorders visuals on Move and creates/destroys visuals on
    /// Insert/Remove. No Replace or Clear+Add anywhere — every visible slot
    /// always shows the data of the symbol it claims to.
    /// </summary>
    private static void SyncList(ObservableCollection<MoverRow> target,
        IList<MoverSnapshot> src, Dictionary<string, MoverRow> rowsBySymbol)
    {
        // Build the desired-order MoverRow list, reusing existing instances by
        // symbol so unchanged entries keep the same reference (and CollectionView
        // keeps the same visual row).
        var desired = new List<MoverRow>(src.Count);
        var presentSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in src)
        {
            presentSymbols.Add(s.Symbol);
            if (rowsBySymbol.TryGetValue(s.Symbol, out var row))
            {
                row.UpdateFrom(s);
            }
            else
            {
                row = new MoverRow(s);
                rowsBySymbol[s.Symbol] = row;
            }
            desired.Add(row);
        }

        // Drop rows whose symbol fell out of the top-N.
        var stale = rowsBySymbol.Keys.Where(s => !presentSymbols.Contains(s)).ToList();
        foreach (var s in stale) rowsBySymbol.Remove(s);

        // Sync target to desired in place. For each position i, ensure
        // target[i] == desired[i] using only Move / Insert / Add / Remove.
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

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }
    #endregion
}
