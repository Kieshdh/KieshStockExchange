using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace KieshStockExchange.Services.Implementations;

public sealed partial class TrendingService : ObservableObject, ITrendingService
{
    private readonly ConcurrentDictionary<int, LiveQuote> _quotes = new();
    private readonly ReadOnlyDictionary<int, LiveQuote> _quotesReadOnly;
    private readonly IDispatcher _dispatcher; // to marshal changes to UI thread
    private readonly Timer _moversTimer;

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

        public IEnumerable<(decimal p, DateTime t)> EnumerateNewestFirst()
        {
            for (int i = 0; i < Count; i++) 
            { 
                int idx = (Head - 1 - i + Prices.Length) % Prices.Length; 
                yield return (Prices[idx], Times[idx]); 
            }
        }
    }

    private readonly ConcurrentDictionary<int, Ring> _rings = new();

    // Derived collections (read-only wrappers for binding)
    private readonly ObservableCollection<LiveQuote> _topGainers = new();
    private readonly ObservableCollection<LiveQuote> _topLosers = new();
    private readonly ObservableCollection<LiveQuote> _mostActive = new();
    public IReadOnlyList<LiveQuote> TopGainers => new ReadOnlyObservableCollection<LiveQuote>(_topGainers);
    public IReadOnlyList<LiveQuote> TopLosers => new ReadOnlyObservableCollection<LiveQuote>(_topLosers);
    public IReadOnlyList<LiveQuote> MostActive => new ReadOnlyObservableCollection<LiveQuote>(_mostActive);

    public event EventHandler<LiveQuote>? QuoteUpdated;

    public TrendingService(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _quotesReadOnly = new ReadOnlyDictionary<int, LiveQuote>(_quotes);
        // Recompute movers every 2 seconds (tweak as needed).
        _moversTimer = new Timer(_ => RecomputeMovers(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public IReadOnlyDictionary<int, LiveQuote> Quotes => _quotesReadOnly;

    public async Task SubscribeAsync(int stockId, CancellationToken ct = default)
    {
        // Lazily create a LiveQuote slot if not present.
        _quotes.GetOrAdd(stockId, id => new LiveQuote { StockId = id });
        _rings.GetOrAdd(stockId, _ => new Ring(capacity: 600)); // ~10 minutes if 1s ticks
        await Task.CompletedTask;
    }

    public void Unsubscribe(int stockId)
    {
        _quotes.TryRemove(stockId, out _);
        _rings.TryRemove(stockId, out _);
        // (Optionally) also remove from movers lists next recompute.
    }

    public void OnTick(StockPrice tick)
    {
        // 1) Update live quote
        var quote = _quotes.GetOrAdd(tick.StockId, id => new LiveQuote { StockId = id, Currency = tick.CurrencyType });
        var utc = DateTime.SpecifyKind(tick.Timestamp, DateTimeKind.Utc);

        // Keep ring for rolling stats
        var ring = _rings.GetOrAdd(tick.StockId, _ => new Ring(600));
        ring.Add(tick.Price, utc);

        // Marshal property changes to UI thread (MAUI requirement for bound objects)
        _dispatcher.Dispatch(() =>
        {
            quote.ApplyTick(tick.Price, utc);
            quote.Currency = tick.CurrencyType;
            QuoteUpdated?.Invoke(this, quote);
        });
    }

    public void RecomputeMovers()
    {
        // Take a moment-in-time snapshot to avoid locking during sort
        var snapshot = _quotes.Values.ToList();

        var gainers = snapshot.Where(q => q.Open > 0m)
                              .OrderByDescending(q => q.ChangePct)
                              .Take(20) // tweak
                              .ToList();

        var losers = snapshot.Where(q => q.Open > 0m)
                              .OrderBy(q => q.ChangePct)
                              .Take(20)
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
        // cheap diff: clear + add (can be improved later)
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
            await Task.Delay(250, ct); // small cadence
        }

        static DateTime Align(DateTime t, TimeSpan bucket)
        {
            var ticks = (t.Ticks / bucket.Ticks) * bucket.Ticks;
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }
}