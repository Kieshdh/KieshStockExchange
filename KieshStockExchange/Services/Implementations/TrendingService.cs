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

public sealed partial class TrendingService : ObservableObject, ITrendingService
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

    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(5));
    private readonly CancellationTokenSource _cts = new();
    public TrendingService(IDispatcher dispatcher, ILogger<TrendingService> logger, IDataBaseService db, IMarketDataService market)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _market = market ?? throw new ArgumentNullException(nameof(market));
        //_moversTimer = new Timer(_ => RecomputeMovers(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));

        _topGainersView = new ReadOnlyObservableCollection<LiveQuote>(_topGainers);
        _topLosersView = new ReadOnlyObservableCollection<LiveQuote>(_topLosers);
        _mostActiveView = new ReadOnlyObservableCollection<LiveQuote>(_mostActive);

        _market.QuoteUpdated += (_, __) => _ = RecomputeMoversAsync(); // debounce via timer loop below
        _ = RunLoopAsync(_cts.Token);
    }
    #endregion

    #region Core
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (await _timer.WaitForNextTickAsync(ct))
            await RecomputeMoversAsync();
    }

    private Task RecomputeMoversAsync()
    {
        const int TAKE = 5;
        var snap = _md.Quotes.Values.ToList();

        var gainers = snap.Where(q => q.Open > 0m)
                          .OrderByDescending(q => q.ChangePct)
                          .Take(TAKE)
                          .ToList();

        var losers = snap.Where(q => q.Open > 0m)
                         .OrderBy(q => q.ChangePct)
                         .Take(TAKE)
                         .ToList();

        _dispatcher.Dispatch(() =>
        {
            Replace(_topGainers, gainers);
            Replace(_topLosers, losers);
            // TODO: MostActive once you track volume
        });

        return Task.CompletedTask;

        static void Replace(ObservableCollection<LiveQuote> target, IList<LiveQuote> src)
        {
            target.Clear();
            foreach (var x in src) target.Add(x);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }
    #endregion
}