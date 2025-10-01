using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace KieshStockExchange.Services.Implementations;

public partial class MarketDataService : ObservableObject, IMarketDataService, IDisposable
{
    #region Fields and Constructor
    private readonly IDispatcher _dispatcher; // to marshal changes to UI thread
    private readonly ILogger<MarketDataService> _logger;
    private readonly IDataBaseService _db;

    public MarketDataService(IDispatcher dispatcher, ILogger<MarketDataService> logger, IDataBaseService db)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _quotesView = new ReadOnlyDictionary<(int, CurrencyType), LiveQuote>(_quotes);
    }
    #endregion

    #region Live Quotes Dictionaries and Subscriptions
    // The single source of truth for all live quotes
    private readonly ConcurrentDictionary<(int, CurrencyType), LiveQuote> _quotes = new();
    private readonly ReadOnlyDictionary<(int, CurrencyType), LiveQuote> _quotesView;
    public IReadOnlyDictionary<(int, CurrencyType), LiveQuote> Quotes => _quotesView;

    // Event fired when a quote is updated, debounced to avoid flooding
    public event EventHandler<LiveQuote>? QuoteUpdated;

    // Track subscriptions and their reference counts
    private readonly ConcurrentDictionary<(int, CurrencyType), int> _subRefCount = new();
    public IReadOnlyCollection<(int, CurrencyType)> Subscribed =>
        _subRefCount.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList().AsReadOnly();
    #endregion

    #region Timers for debouncing QuoteUpdated event
    // One timer per (stockId, currency) pair, created on demand
    private readonly ConcurrentDictionary<(int, CurrencyType), IDispatcherTimer> _timers = new();

    // Keep track of event handlers to allow removal
    private readonly ConcurrentDictionary<(int, CurrencyType), EventHandler> _timerHandlers = new();

    // One timer per (stockId, currency) pair for simulating random ticks
    private readonly ConcurrentDictionary<(int, CurrencyType), IDispatcherTimer> _simTimers = new();

    // Debounce interval
    private static readonly TimeSpan TickerInterval = TimeSpan.FromMilliseconds(250);
    #endregion

    #region Ring buffer
    // One ring buffer per (stockId, currency) pair, created on demand
    private readonly ConcurrentDictionary<(int, CurrencyType), RingBuffer> _rings = new();
    private readonly ConcurrentDictionary<(int, CurrencyType), object> _ringGates = new();

    // Ticker interval resolution
    private static RingBufferDuration CurrentDuration = RingBufferDuration.FiveMinutes;

    private static readonly Dictionary<RingBufferDuration, TimeSpan> DurationMap = new()
    {
        { RingBufferDuration.OneMinute, TimeSpan.FromMinutes(1) },
        { RingBufferDuration.FiveMinutes, TimeSpan.FromMinutes(5) },
        { RingBufferDuration.FifteenMinutes, TimeSpan.FromMinutes(15) },
        { RingBufferDuration.OneHour, TimeSpan.FromHours(1) }
    };

    private static int RingCapacity =>
        (int)(DurationMap[CurrentDuration].TotalMilliseconds / TickerInterval.TotalMilliseconds);

    /// <summary> Change the duration for which ticks are stored in the ring buffer. </summary>
    public void ChangeStoreDuration(RingBufferDuration duration)
    {
        // Set the new duration and adjust existing rings
        CurrentDuration = duration;

        // Compute new capacity
        var newCapacity = RingCapacity;

        // Reset all rings to new capacity
        foreach (var kv in _rings.ToArray())
        {
            // Skip if capacity is unchanged
            var oldRing = kv.Value;
            if (oldRing.Capacity == newCapacity) continue; // No change needed

            // Create a new ring with the new capacity and copy over existing entries
            var newRing = new RingBuffer(newCapacity);

            lock (GetRingGate(kv.Key))
            {
                // Create a new ring with the new capacity and copy over existing entries
                foreach (var entry in oldRing.EnumerateOldestFirst())
                    newRing.Add(entry.Price, entry.Time);

                // Replace the old ring with the new one
                _rings[kv.Key] = newRing;
            }
        }
    }

    private RingBuffer GetRing((int stockId, CurrencyType currency) key)
        => _rings.GetOrAdd(key, _ => new RingBuffer(RingCapacity));

    private RingBuffer? TryGetRing((int stockId, CurrencyType currency) key)
        => _rings.TryGetValue(key, out var ring) ? ring : null;

    private object GetRingGate((int stockId, CurrencyType currency) key) => 
        _ringGates.GetOrAdd(key, _ => new object());

    private void AddToRing((int stockId, CurrencyType currency) key, decimal price, DateTime utc)
    {
        lock (GetRingGate(key))
        {
            var ring = GetRing(key);
            ring.Add(price, utc);
        }
    }
    #endregion

    #region Stock details
    // Simple in-memory cache of all stocks
    private readonly ConcurrentDictionary<int, Stock> _stockCache = new();

    public async Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default)
    {
        // Try cache first
        if (_stockCache.TryGetValue(stockId, out var stock))
            return stock;
        // Refresh cache
        await GetAllStocksAsync(ct);
        // Try again
        _stockCache.TryGetValue(stockId, out stock);
        return stock;
    }

    // Get all stocks and populate cache
    public async Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        var stocks = await _db.GetStocksAsync(ct);
        foreach (var stock in stocks)
            _stockCache[stock.StockId] = stock;
        return stocks.AsReadOnly();
    }

    // Get last price from LiveQuote or fallback to DB
    public async Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Try LiveQuote first
        var quote = await GetOrAddQuote(stockId, currency, ct);
        if (quote.LastPrice > 0m)
            return quote.LastPrice;

        // Fallback to latest Transaction from DB
        var tx = await _db.GetLatestTransactionByStockId(stockId, currency, ct);
        if (tx is not null && tx.Price > 0m)
            return tx.Price;

        // Fallback to latest StockPrice from DB
        var sp = await _db.GetLatestStockPriceByStockId(stockId, currency, ct);
        return sp?.Price ?? 0m;
    }
    #endregion

    #region Subscribe/Unsubscribe
    public async Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await GetOrAddQuote(stockId, currency, ct);
        _subRefCount.AddOrUpdate((stockId, currency), 1, (_, c) => c + 1);
    }

    public void Unsubscribe(int stockId, CurrencyType currency)
    {
        var key = (stockId, currency);
        // Decrement ref count
        var count = _subRefCount.AddOrUpdate(key, 0, (_, c) => Math.Max(0, c - 1));
        // If no more subscribers, clean up
        if (count == 0)
        {
            // Remove quote and ring
            _quotes.TryRemove(key, out _);
            _rings.TryRemove(key, out _);
            _ringGates.TryRemove(key, out _);
            _subRefCount.TryRemove(key, out _);

            // Stop Timers if running
            if (_timers.TryRemove(key, out var timer))
                _dispatcher.Dispatch(() =>
                {
                    if (_timerHandlers.TryRemove(key, out var handler))
                        timer.Tick -= handler;
                    timer.Stop();
                });
            if (_simTimers.TryRemove(key, out var sim))
                _dispatcher.Dispatch(() => sim.Stop());
        }
    }

    public async Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
    {
        var stocks = await GetAllStocksAsync(ct);
        foreach (var stock in stocks)
            await SubscribeAsync(stock.StockId, currency, ct);
    }
    #endregion

    #region Build LiveQuote from ticks
    public async Task OnTick(Transaction tick, CancellationToken ct = default)
    {
        if (!tick.IsValid())
        {
            _logger.LogWarning("Invalid tick received: {@Tick}", tick);
            return;
        }

        var stockId = tick.StockId;
        var currency = tick.CurrencyType;
        var key = (stockId, currency);

        // Ignore if not subscribed
        if (!_subRefCount.TryGetValue((stockId, currency), out var c) || c <= 0)
            return; // Nobody subscribed

        // Update live quote
        var quote = await GetOrAddQuote(stockId, currency, ct);
        var utc = TimeHelper.EnsureUtc(tick.Timestamp);

        // Keep ring for rolling stats
        AddToRing(key, tick.Price, utc);

        // Dispatch the update
        _dispatcher.Dispatch(() =>
        {
            quote.ApplyTick(tick.Price, tick.Quantity, utc);
            ScheduleQuoteUpdated(stockId, currency);
        });
    }

    public async Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var key = (stockId, currency);
        // Get the quote and ring
        var quote = await GetOrAddQuote(stockId, currency, ct);
        var ring = GetRing(key);

        // If we already have data, skip
        if (ring.IsNotEmpty)
            return;

        // Load the current day of historical prices
        var history = await GetHistoricalTicksAsync(stockId, currency, ct);
        if (history.Count == 0)
        {
            _logger.LogWarning("No historical ticks found for stock {StockId} in {Currency}", stockId, currency);
            return;
        }

        // Set initial OHLC from history
        decimal open = history[0].Price;
        decimal high = history[0].Price;
        decimal low = history[0].Price;

        // Fill the ring and compute OHLC
        lock (GetRingGate(key))
        {
            foreach (var tick in history)
            {
                var dt = TimeHelper.EnsureUtc(tick.Timestamp);
                ring.Add(tick.Price, dt);
                if (tick.Price > high) high = tick.Price;
                if (tick.Price < low) low = tick.Price;
            }
        }

        _dispatcher.Dispatch(() =>
        {
            var lastUtc = TimeHelper.EnsureUtc(history[^1].Timestamp);
            var volume = history.Sum(t => t.Quantity);
            var last = history[^1].Price;

            // Apply data to quote
            quote.ApplySnapshot(last, volume, open, high, low, lastUtc);

            // Notify listeners
            ScheduleQuoteUpdated(stockId, currency);
        });
    }

    private async Task<List<Transaction>> GetHistoricalTicksAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Load last day of historical transactions
        var (start, end) = TimeHelper.TodayUtcRange();

        var history = await _db.GetTransactionsByStockIdAndTimeRange(stockId, currency, start, end, ct);
        // If no history, fallback to latest StockPrice as a zero-quantity tick
        if (history.Count == 0)
        {
            _logger.LogWarning("No transactions found for stock {StockId} in {Currency}, falling back to StockPrice", stockId, currency);
            var latestPrice = await _db.GetLatestStockPriceByStockId(stockId, currency, ct);
            if (latestPrice != null)
                history.Add(new Transaction
                {
                    StockId = stockId,
                    CurrencyType = currency,
                    Price = latestPrice.Price,
                    Quantity = 0,
                    Timestamp = latestPrice.Timestamp
                });
            else
                _logger.LogWarning("No fallback price found for stock {StockId} in {Currency}", stockId, currency);
        }
        // Sort by time ascending
        history.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return history;
    }
    #endregion

    #region Stream Candles
    private sealed class CandleBucketState
    {
        public DateTime? StartUtc;          // Start of the current candle bucket (aligned)
        public decimal O, H, L, C;          // Working OHLC
        public bool HasActiveBucket => StartUtc.HasValue;
        
        private CurrencyType _currency = CurrencyType.USD;
        private int _stockId = 0;

        public CurrencyType Currency => _currency;
        public int StockId => _stockId;

        public CandleBucketState(int stockId, CurrencyType currency)
        {
            _currency = currency;
            _stockId = stockId;
        }

        public void Begin(DateTime startUtc, decimal price)
        {
            StartUtc = startUtc;
            O = H = L = C = price;
        }

        public Candle ToCandle(TimeSpan bucket) =>
            new Candle {
                StockId = _stockId, CurrencyType = _currency,
                OpenTime = StartUtc!.Value,
                ResolutionSeconds = (int)bucket.TotalSeconds,
                Open = O, High = H, Low = L, Close = C,
                Volume = 0, TradeCount = 0
            };
    }

    public async IAsyncEnumerable<Candle> StreamCandlesAsync(
        int stockId, CurrencyType currency, TimeSpan bucket, bool fillGaps = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = (stockId, currency);
        var state = new CandleBucketState(stockId, currency);
        DateTime? lastProcessed = null;

        // Initialize from ring
        lastProcessed = EnsureInitializedLastProcessed(key);

        while (!ct.IsCancellationRequested)
        {
            // Only for new entries since last processed
            var newEntries = TryGetNewRingEntries(key, lastProcessed);
            if (newEntries.Count > 0)
            {
                // Fold them into OHLC buckets and emit completed candles
                foreach (var entry in newEntries)
                {
                    // Consume the tick into buckets
                    foreach (var candle in ConsumeTickIntoBuckets(
                                 bucket, entry.Time, entry.Price, fillGaps, state))
                        yield return candle;

                    // Update last processed to this entry
                    lastProcessed = entry.Time;
                }
            }

            // Optionally fill real-time gaps with flat candles up to "now"
            if (fillGaps && state.HasActiveBucket)
            {
                foreach (var gap in EmitGapCandlesUpToNow(stockId, bucket, state))
                    yield return gap;
            }

            // Wait for next tick interval
            await Task.Delay(TickerInterval, ct);
        }
    }

    private DateTime? EnsureInitializedLastProcessed((int stockId, CurrencyType currency) key)
    {
        var ring = TryGetRing(key);
        if (ring == null || ring.IsEmpty)
            return null;

        lock (GetRingGate(key))
        {
            var oldest = ring.TryPeekOldest();
            if (oldest is not null)
                // "One tick before oldest" ensures we process the oldest entry too.
                return oldest.Value.Time.AddTicks(-1);
        }
        return null;
    }
    
    private List<RingEntry> TryGetNewRingEntries(
        (int stockId, CurrencyType currency) key, DateTime? lastProcessed)
    {
        var result = new List<RingEntry>();

        // Get the ring
        var ring = TryGetRing(key);
        if (ring == null || ring.IsEmpty)
            return result;

        lock (GetRingGate(key))
        {
            // We enumerate newest first and when we hit lastProcessed we stop.
            foreach (var entry in ring.EnumerateNewestFirst())
            {
                // Stop if we reached last processed
                if (lastProcessed is not null && entry.Time <= lastProcessed.Value) 
                    break;
                result.Add(entry);
            }
        }

        // Reverse to get oldest first
        result.Reverse();
        return result;
    }

    private IEnumerable<Candle> ConsumeTickIntoBuckets(TimeSpan bucket, DateTime tickUtc, decimal price, bool fillGaps, CandleBucketState state)
    {
        var outCandles = new List<Candle>(); // typical: 0 or 1, rare: +gap fillers
        var start = TimeHelper.FloorToBucketUtc(tickUtc, bucket); 

        // No active bucket yet: start one
        if (!state.HasActiveBucket)
        {
            state.Begin(start, price);
            return outCandles;
        }

        // Same bucket: update OHLC
        if (start == state.StartUtc)
        {
            state.C = price;
            if (price > state.H) state.H = price;
            if (price < state.L) state.L = price;
            return outCandles;
        }

        // Bucket rolled: emit the finished candle
        outCandles.Add(state.ToCandle(bucket));

        // Optionally fill fully-missing buckets with flat candles at prior close
        if (fillGaps)
        {
            var next = state.StartUtc!.Value + bucket;
            while (next < start)
            {
                outCandles.Add(CreateCandle(state.StockId, state.Currency, next, bucket, state.C, state.C, state.C, state.C));
                next += bucket;
            }
        }

        // Begin the new bucket with the current price
        state.Begin(start, price);
        return outCandles;
    }

    private IEnumerable<Candle> EmitGapCandlesUpToNow(
    int stockId, TimeSpan bucket, CandleBucketState state)
    {
        //
        var result = new List<Candle>();
        var start = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), bucket);

        // No active bucket: nothing to do
        if (!state.HasActiveBucket)
            return result;

        // If our current start is behind now's bucket, fill forward with flat candles.
        while (state.StartUtc!.Value < start)
        {
            result.Add(state.ToCandle(bucket));
            // Advance one bucket, “carry” last close as new O/H/L/C
            var nextStart = state.StartUtc.Value + bucket;
            var c = state.C;
            state.Begin(nextStart, c);
        }
        return result;
    }
    #endregion

    #region Random Display Ticker (for testing/demo)
    private const decimal Percentage = 0.01m;

    public void StartRandomDisplayTicker(int stockId, CurrencyType currency) =>
        _simTimers.GetOrAdd((stockId, currency), _ =>
        {
            var t = _dispatcher.CreateTimer();
            t.Interval = TickerInterval;
            t.IsRepeating = true;
            t.Tick += (_, __) => SimulateOneTick(stockId, currency);
            t.Start();
            return t;
        });

    public void StopRandomDisplayTicker(int stockId, CurrencyType currency)
    {
        var key = (stockId, currency);
        if (_simTimers.TryRemove(key, out var timer))
            _dispatcher.Dispatch(() => timer.Stop());
    }

    private void SimulateOneTick(int stockId, CurrencyType currency)
    {
        // Get the quote
        if (!_quotes.TryGetValue((stockId, currency), out var q)) return;

        var now = TimeHelper.NowUtc();

        // Seed price if we have nothing yet (history hasn’t filled in)
        var last = q.LastPrice;
        if (last <= 0m)
            last = q.Open > 0m ? q.Open : 100m; // simple seed

        // ±1% random move
        var factor = 1m + (decimal)(Random.Shared.NextDouble() * 2 - 1) * Percentage;
        var next = Math.Max(0.01m, last * factor);
        var shares = Random.Shared.Next(1, 100);

        // Update ring off-UI (thread-safe)
        var key = (stockId, currency);
        AddToRing(key, next, now);

        // Update LiveQuote on UI thread
        _dispatcher.Dispatch(() =>
        {
            q.ApplyTick(next, shares, now);
            ScheduleQuoteUpdated(stockId, currency);
        });
    }
    #endregion

    #region Private Helpers
    private async Task<LiveQuote> GetOrAddQuote(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Get or create the LiveQuote
        var quote = _quotes.GetOrAdd((stockId, currency), _ => new LiveQuote(stockId, currency));

        try
        {
            // If still no symbol, fetch stock details
            if (quote.Symbol == "-")
            {
                var stock = await GetStockAsync(stockId, ct);
                if (stock != null)
                    _dispatcher.Dispatch(() =>
                    {
                        quote.Symbol = stock.Symbol;
                        quote.CompanyName = stock.CompanyName;
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing LiveQuote for stock {StockId} in {Currency}", stockId, currency);
        }

        return quote;
    }

    private void ScheduleQuoteUpdated(int stockId, CurrencyType currency)
    {
        // Debounce rapid updates to avoid UI flooding
        var key = (stockId, currency);
        var timer = _timers.GetOrAdd(key, _ =>
        {
            var t = _dispatcher.CreateTimer();
            t.Interval = TickerInterval;
            t.IsRepeating = false;
            EventHandler handler = (_, __) =>
            {
                if (_quotes.TryGetValue(key, out var q))
                {
                    try { QuoteUpdated?.Invoke(this, q); }
                    catch (Exception ex) { _logger.LogError(ex, "QuoteUpdated handler threw."); }
                }
            };

            t.Tick += handler;
            _timerHandlers[key] = handler;
            return t;
        });

        // Dispatch the update
        _dispatcher.Dispatch(() =>
        {
            if (timer.IsRunning)
                timer.Stop();
            timer.Start();
        });
    }

    private Candle CreateCandle(int stockId, CurrencyType currency, DateTime startUtc, TimeSpan bucket, 
        decimal open, decimal high, decimal low, decimal close, int volume = 0, int trades = 0) =>
        new Candle {
            StockId = stockId, CurrencyType = currency, OpenTime = startUtc,
            ResolutionSeconds = (int)bucket.TotalSeconds,
            Open = open, High = high, Low = low, Close = close,
            Volume = volume, TradeCount = trades,
        };
    #endregion

    #region IDisposable 
    private bool Disposed = false;

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        try
        {
            // Stop & detach all debounce timers created in ScheduleQuoteUpdated
            foreach (var kv in _timers.ToArray())
                StopAndDetachTimer(kv.Key, kv.Value);

            // Stop all simulated random-tick timers used by StartRandomDisplayTicker
            foreach (var kv in _simTimers.ToArray())
                StopAndDetachSimTimer(kv.Key, kv.Value);

            // Clear all event handlers to prevent memory leaks
            QuoteUpdated = null;

            // Clear all dictionaries
            _timerHandlers.Clear();
            _quotes.Clear();
            _rings.Clear();
            _ringGates.Clear();
            _subRefCount.Clear();
            _stockCache.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing MarketDataService.");
        }

    }

    private void StopAndDetachTimer((int, CurrencyType) key, IDispatcherTimer timer)
    {
        _dispatcher.Dispatch(() =>
        {
            if (_timerHandlers.TryRemove(key, out var handler))
                timer.Tick -= handler;
            if (timer.IsRunning)
                timer.Stop();
        });
        _timers.TryRemove(key, out _);
    }

    private void StopAndDetachSimTimer((int, CurrencyType) key, IDispatcherTimer timer)
    {
        _dispatcher.Dispatch(() =>
        {
            if (timer.IsRunning)
                timer.Stop();
        });

        _simTimers.TryRemove(key, out _);
    }

    #endregion
}
