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
    #region Fields and Properties
    private readonly IDispatcher _dispatcher; // to marshal changes to UI thread
    private readonly ILogger<TrendingService> _logger;
    private readonly IDataBaseService _db;

    private readonly ConcurrentDictionary<(int, CurrencyType), LiveQuote> _quotes = new();
    private readonly ReadOnlyDictionary<(int, CurrencyType), LiveQuote> _quotesReadOnly;
    private readonly Timer _moversTimer;

    // Derived collections (read-only wrappers for binding)
    private readonly ObservableCollection<LiveQuote> _topGainers = new();
    private readonly ObservableCollection<LiveQuote> _topLosers = new();
    private readonly ObservableCollection<LiveQuote> _mostActive = new();
    #endregion

    #region Public Properties
    public IReadOnlyDictionary<(int, CurrencyType), LiveQuote> Quotes => _quotesReadOnly;
    public event EventHandler<LiveQuote>? QuoteUpdated;

    public IReadOnlyList<LiveQuote> TopGainers => new ReadOnlyObservableCollection<LiveQuote>(_topGainers);
    public IReadOnlyList<LiveQuote> TopLosers => new ReadOnlyObservableCollection<LiveQuote>(_topLosers);
    public IReadOnlyList<LiveQuote> MostActive => new ReadOnlyObservableCollection<LiveQuote>(_mostActive);
    #endregion

    #region Internal ring buffer and Constructor
    // ring buffer per stock for simple rolling metrics (prices + timestamps)
    private sealed class Ring
    {
        public readonly decimal[] Prices;
        public readonly DateTime[] Times;
        public int Head = 0; 
        public int Count = 0;

        public Ring(int capacity) 
        { 
            Prices = new decimal[capacity]; 
            Times = new DateTime[capacity]; 
        }

        public void Add(decimal price, DateTime time) { 
            Prices[Head] = price; 
            Times[Head] = time; 
            Head = (Head + 1) % Prices.Length; 
            Count = Math.Min(Count + 1, Prices.Length); 
        }

        public void SortByTime() => Array.Sort(Times, Prices, 0, Count);

        public IEnumerable<(decimal p, DateTime t)> EnumerateNewestFirst()
        {
            SortByTime();
            for (int i = 0; i < Count; i++) 
            { 
                int idx = (Head - 1 - i + Prices.Length) % Prices.Length; 
                yield return (Prices[idx], Times[idx]); 
            }
        }
    }

    private readonly ConcurrentDictionary<(int, CurrencyType), Ring> _rings = new();

    public TrendingService(IDispatcher dispatcher, ILogger<TrendingService> logger, IDataBaseService db)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _quotesReadOnly = new ReadOnlyDictionary<(int, CurrencyType), LiveQuote>(_quotes);
        // Recompute movers every 2 seconds (tweak as needed).
        _moversTimer = new Timer(_ => RecomputeMovers(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }
    #endregion

    #region Subscribe/Unsubscribe
    public async Task SubscribeAsync(int stockId, CancellationToken ct = default)
    {
        // Lazily create a LiveQuote slot if not present.
        _quotes.GetOrAdd(stockId, id => new LiveQuote { StockId = id });
        _rings.GetOrAdd(stockId, _ => new Ring(capacity: 600));
        await Task.CompletedTask;
    }

    public void Unsubscribe(int stockId)
    {
        _quotes.TryRemove(stockId, out _);
        _rings.TryRemove(stockId, out _);
    }

    public async Task SubscribeAllAsync(CancellationToken ct = default)
    {
        var Stocks = await _db.GetStocksAsync(ct);
        foreach (var stock in Stocks)
            await SubscribeAsync(stock.StockId, ct);
    }
    #endregion

    #region Build LiveQuote from ticks
    public async Task OnTick(StockPrice tick)
    {
        var stockId = tick.StockId;
        var currency = tick.CurrencyType;

        // 1) Update live quote
        var quote = await GetOrAddQuote(stockId, currency);
        var utc = DateTime.SpecifyKind(tick.Timestamp, DateTimeKind.Utc);

        // Keep ring for rolling stats
        var ring = _rings.GetOrAdd((stockId, currency), _ => new Ring(600));
        ring.Add(tick.Price, utc);

        // Marshal property changes to UI thread (MAUI requirement for bound objects)
        _dispatcher.Dispatch(() =>
        {
            quote.ApplyTick(tick.Price, utc);
            QuoteUpdated?.Invoke(this, quote);
        });
    }

    public async Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Load last 24 hours of historical prices
        var history = await _db.GetStockPricesByStockIdAndTimeRange(
            stockId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, currency, ct);
        if (history == null || history.Count == 0)
        {
            _logger.LogWarning("No historical prices found for stock {StockId} in {Currency}", stockId, currency);
            // Try to get the latest price as a fallback
            history = new List<StockPrice>();
            var latest = await _db.GetLatestStockPriceByStockId(stockId, currency, ct);
            if (latest != null) history.Add(latest);
            else throw new InvalidOperationException($"No prices found for stock {stockId} in {currency}");
        }

        // Feed each tick to OnTick to build the LiveQuote and ring buffer
        foreach (var tick in history)
            OnTick(tick);
    }

    public void RecomputeMovers()
    {
        // Take a moment-in-time snapshot to avoid locking during sort
        int STOCKSVIEW = 5;
        var snapshot = _quotes.Values.ToList();

        var gainers = snapshot.Where(q => q.Open > 0m)
                              .OrderByDescending(q => q.ChangePct)
                              .Take(STOCKSVIEW)
                              .ToList();

        var losers = snapshot.Where(q => q.Open > 0m)
                              .OrderBy(q => q.ChangePct)
                              .Take(STOCKSVIEW)
                              .ToList();

        // TODO: MostActive requires volume; once volume is tracked, sort by volume here.

        _dispatcher.Dispatch(() =>
        {
            Replace(_topGainers, gainers);
            Replace(_topLosers, losers);
        });
    }

    private static void Replace(ObservableCollection<LiveQuote> target, IList<LiveQuote> src)
    {
        target.Clear();
        foreach (var q in src) target.Add(q);
    }

    public async IAsyncEnumerable<Candle> StreamCandlesAsync(int stockId, TimeSpan bucket, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Simple aggregator: compute OHLC for each completed bucket.
        DateTime? bucketStart = null;
        decimal o = 0, h = 0, l = 0, c = 0;

        // Feed from the ring rather than a channel for simplicity; in a real impl,
        // hook this to a Channel<StockPrice> per stock.
        DateTime lastEmitted = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            // Poll the latest ring entry and aggregate
            if (_rings.TryGetValue(stockId, out var ring) && ring.Count > 0)
            {
                var (p, t) = ring.EnumerateNewestFirst().First();
                var start = Align(t, bucket);
                if (bucketStart == null) { bucketStart = start; o = h = l = c = p; }
                if (start == bucketStart) { c = p; h = Math.Max(h, p); l = Math.Min(l, p); }
                else
                {
                    // emit the finished candle
                    yield return new Candle(stockId, bucketStart.Value, bucket, o, h, l, c);
                    // start new bucket
                    bucketStart = start; o = h = l = c = p;
                    lastEmitted = DateTime.UtcNow;
                }
            }
            await Task.Delay(250, ct);
        }

        static DateTime Align(DateTime t, TimeSpan bucket)
        {
            var ticks = (t.Ticks / bucket.Ticks) * bucket.Ticks;
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    private async Task<LiveQuote> GetOrAddQuote(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Get or create the LiveQuote
        var quote = _quotes.GetOrAdd((stockId, currency), _ => new LiveQuote { StockId = stockId, Currency = currency });

        // If no price yet, try to build from history
        if (quote.LastPrice == 0m)
            await BuildFromHistoryAsync(stockId, currency, ct);
        // If still no symbol, fetch stock details
        if (quote.Symbol == string.Empty)
        {
            var stock = await _db.GetStockById(stockId, ct);
            if (stock != null)
            {
                quote.Symbol = stock.Symbol;
                quote.CompanyName = stock.CompanyName;
            }
        }
        return quote;
    }
        

}