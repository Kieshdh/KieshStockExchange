using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace KieshStockExchange.Services.Implementations;

public sealed partial class TrendingService : ObservableObject, ITrendingService, IDisposable
{
    #region Movers Lists
    private readonly ObservableCollection<LiveQuote> _topGainers = new();
    private readonly ObservableCollection<LiveQuote> _topLosers = new();
    private readonly ObservableCollection<LiveQuote> _mostActive = new();

    private readonly ReadOnlyObservableCollection<LiveQuote> _topGainersView;
    private readonly ReadOnlyObservableCollection<LiveQuote> _topLosersView;
    private readonly ReadOnlyObservableCollection<LiveQuote> _mostActiveView;

    public IReadOnlyList<LiveQuote> TopGainers => _topGainersView;
    public IReadOnlyList<LiveQuote> TopLosers => _topLosersView;
    public IReadOnlyList<LiveQuote> MostActive => _mostActiveView;
    #endregion

    #region Fields and Constructor
    private readonly IDispatcher _dispatcher; // to marshal changes to UI thread
    private readonly ILogger<TrendingService> _logger;
    private readonly IDataBaseService _db;
    private readonly IMarketDataService _market;

    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(SecondsBetweenUpdates));
    private readonly CancellationTokenSource _cts = new();
    private readonly EventHandler<LiveQuote> _onQuoteUpdated;

    private const int MaxMovers = 5;
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
        _topGainersView = new ReadOnlyObservableCollection<LiveQuote>(_topGainers);
        _topLosersView = new ReadOnlyObservableCollection<LiveQuote>(_topLosers);
        _mostActiveView = new ReadOnlyObservableCollection<LiveQuote>(_mostActive);

        // React to live quote pushes from the single source of truth
        _onQuoteUpdated = (_, __) => _ = RecomputeMoversAsync();
        _market.QuoteUpdated += _onQuoteUpdated;

        // Subscribe to all stocks in the specified currency
        _ = _market.SubscribeAllAsync(Currency);

        // Start the periodic recompute loop
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
        var snap = _market.Quotes.Values.ToList();

        var gainers = snap.Where(q => q.Open > 0m)
            .OrderByDescending(q => q.ChangePct).Take(MaxMovers).ToList();

        var losers = snap.Where(q => q.Open > 0m)
            .OrderBy(q => q.ChangePct).Take(MaxMovers).ToList();

        var actives = snap.Where(q => q.Volume > 0)
            .OrderByDescending(q => q.Volume).Take(MaxMovers).ToList();

        _dispatcher.Dispatch(() =>
        {
            Replace(_topGainers, gainers);
            Replace(_topLosers, losers);
            Replace(_mostActive, actives);
        });

        return Task.CompletedTask;

    }

    public void Dispose()
    {
        _market.QuoteUpdated -= _onQuoteUpdated;
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }

    private static void Replace(ObservableCollection<LiveQuote> target, IList<LiveQuote> src)
    {
        target.Clear();
        foreach (var x in src) 
            target.Add(x);
    }
    #endregion
}