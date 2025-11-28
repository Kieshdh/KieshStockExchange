using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace KieshStockExchange.Services.Implementations;

public partial class MarketDataService : ObservableObject, IMarketDataService, IAsyncDisposable
{
    #region Fields and Constructor
    private readonly IDispatcher _dispatcher; // to marshal changes to UI thread
    private readonly ILogger<MarketDataService> _logger;
    private readonly IDataBaseService _db;
    private readonly ICandleService _candle;
    private readonly IStockService _stock;

    public MarketDataService(IDispatcher dispatcher, ILogger<MarketDataService> logger, 
        IDataBaseService db, ICandleService candle, IStockService stock)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _candle = candle ?? throw new ArgumentNullException(nameof(candle));
        _stock = stock ?? throw new ArgumentNullException(nameof(stock));

        _quotesView = new ReadOnlyDictionary<(int, CurrencyType), LiveQuote>(_quotes);
    }
    #endregion

    #region Live Quotes Dictionaries and Subscriptions
    // The single source of truth for all live quotes. Key: (stockId, currency)
    private readonly ConcurrentDictionary<(int, CurrencyType), LiveQuote> _quotes = new();
    private readonly ReadOnlyDictionary<(int, CurrencyType), LiveQuote> _quotesView;
    public IReadOnlyDictionary<(int, CurrencyType), LiveQuote> Quotes => _quotesView;

    // Event fired when a quote is updated, debounced to avoid flooding
    public event EventHandler<LiveQuote>? QuoteUpdated;

    // Track subscriptions and their reference counts. Key: (stockId, currency)
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
    public async Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default)
    {
        // Ensure loaded once, then retry
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        _stock.TryGetById(stockId, out var stock);
        return stock;
    }

    public async Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _stock.All;
    }

    public async Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Try LiveQuote first
        var quote = await GetOrAddQuote(stockId, currency, ct).ConfigureAwait(false);
        if (quote.LastPrice > 0m) return quote.LastPrice;

        // Try latest price from candles
        var candle = _candle.TryGetLiveSnapshot(stockId, currency, CandleResolution.Default);
        if (candle is not null && candle.Close > 0m) return candle.Close;

        // Fallback to latest Transaction from DB
        var tx = await _db.GetLatestTransactionByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (tx is not null && tx.Price > 0m) return tx.Price;

        _logger.LogDebug("No live price found for stock {StockId} in {Currency}", stockId, currency);

        // Fallback to latest StockPrice from DB
        var sp = await _db.GetLatestStockPriceByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (sp is not null && sp.Price > 0m) return sp.Price;

        // Fallback to USD latest price converted
        if (currency != CurrencyType.USD)
        {
            var usdPrice = await GetLastPriceAsync(stockId, CurrencyType.USD, ct).ConfigureAwait(false);
            if (usdPrice > 0m) return CurrencyHelper.Convert(usdPrice, CurrencyType.USD, currency);
        }

        // Final fallback to default seed price
        return 100m; // Default seed price
    }

    public async Task<decimal> GetDateTimePriceAsync(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
    {
        // Try to get the price at or before the specified time
        var tx = await _db.GetLatestTransactionBeforeTime(stockId, currency, time, ct).ConfigureAwait(false);
        if (tx is not null && tx.Price > 0m) return tx.Price;

        // Fallback to latest StockPrice from DB
        var sp = await _db.GetLatestStockPriceBeforeTime(stockId, currency, time, ct).ConfigureAwait(false);
        if (sp is not null && sp.Price > 0m) return sp.Price;

        // Fallback to USD latest price converted
        if (currency != CurrencyType.USD)
        {
            var usdPrice = await GetDateTimePriceAsync(stockId, CurrencyType.USD, time, ct).ConfigureAwait(false);
            if (usdPrice > 0m) return CurrencyHelper.Convert(usdPrice, CurrencyType.USD, currency);
        }
        return 100m; // Default seed price
    }
    #endregion

    #region Subscribe/Unsubscribe
    public async Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await GetOrAddQuote(stockId, currency, ct).ConfigureAwait(false);
        _subRefCount.AddOrUpdate((stockId, currency), 1, (_, c) => c + 1);

        // Subscribe to candles
        _candle.Subscribe(stockId, currency, CandleResolution.Default);
    }

    public async Task Unsubscribe(int stockId, CurrencyType currency, CancellationToken ct = default)
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

            // Unsubscribe from candles
            await _candle.UnsubscribeAsync(stockId, currency, CandleResolution.Default, ct).ConfigureAwait(false);
        }
    }

    public async Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
    {
        var stocks = await GetAllStocksAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stocks.Select(s => SubscribeAsync(s.StockId, currency, ct))).ConfigureAwait(false);
    }

    public async Task UnsubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
    {
        var stocks = await GetAllStocksAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stocks.Select(s => Unsubscribe(s.StockId, currency, ct))).ConfigureAwait(false);
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

        // Update candles
        await _candle.OnTransactionTickAsync(tick, ct).ConfigureAwait(false);

        // Ignore if not subscribed
        if (!_subRefCount.TryGetValue((stockId, currency), out var c) || c <= 0)
            return; // Nobody subscribed

        // Update live quote
        var quote = await GetOrAddQuote(stockId, currency, ct).ConfigureAwait(false);
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
        var quote = await GetOrAddQuote(stockId, currency, ct).ConfigureAwait(false);
        var ring = GetRing(key);

        // If we already have data, skip
        if (ring.IsNotEmpty)
            return;

        // Load the current day of historical prices
        var history = await GetHistoricalTicksAsync(stockId, currency, ct).ConfigureAwait(false);
        if (history.Count == 0)
        {
            // Get a fallback price if no history
            (decimal price, DateTime time) = await FallbackNoHistoricalTicks(
                stockId, currency, quote, ring, ct).ConfigureAwait(false);

            // Add to ring and update quote
            lock (GetRingGate(key))
                ring.Add(price, time);
            _dispatcher.Dispatch(() =>
            {
                quote.ApplySnapshot(price, 0, price, price, price, time);
                ScheduleQuoteUpdated(stockId, currency);
            });
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

        var history = await _db.GetTransactionsByStockIdAndTimeRange(
            stockId, currency, start, end, ct).ConfigureAwait(false);
        if (history.Count == 0)
            _logger.LogWarning("No transactions found for stock {StockId} in {Currency}", stockId, currency);
        // Sort by time ascending
        history.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return history;
    }
    #endregion

    #region Random Display Ticker (for testing/demo)
    private const decimal Percentage = 0.002m;

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

        // Random move
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

    private async Task<(decimal, DateTime)> FallbackNoHistoricalTicks(int stockId, CurrencyType currency, LiveQuote quote, RingBuffer ring, CancellationToken ct)
    { 
        // Fallback to latest transaction price
        var tx = await _db.GetLatestTransactionByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (tx is not null)
            return (tx.Price, TimeHelper.EnsureUtc(tx.Timestamp));
        
        // Fallback to latest stock price
        var sp = await _db.GetLatestStockPriceByStockId(stockId, currency, ct).ConfigureAwait(false);
        if (sp is not null)
            return (sp.Price, TimeHelper.EnsureUtc(sp.Timestamp));

        _logger.LogWarning("No transactions or stock prices found for stock {StockId} in {Currency}", stockId, currency);

        // Fallback to USD latest price converted
        if (currency != CurrencyType.USD)
        {
            var usdPrice = await GetLastPriceAsync(stockId, CurrencyType.USD, ct).ConfigureAwait(false);
            if (usdPrice > 0m)
                return (CurrencyHelper.Convert(usdPrice, CurrencyType.USD, currency), TimeHelper.NowUtc());
        }

        // Final fallback to default seed price
        _logger.LogWarning("No price data found for stock {StockId} in {Currency}, using default seed price.", stockId, currency);
        return (100m, TimeHelper.NowUtc());
    }
    #endregion

    #region IDisposable 
    private bool Disposed = false;

    public async ValueTask DisposeAsync()
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

            // Ensure underlying candle stream is also torn down
            foreach (var (stockId, currency) in _subRefCount.Keys.ToArray())
            {
                try { await _candle.UnsubscribeAsync(stockId, currency, 
                    CandleResolution.Default).ConfigureAwait(false); }
                catch { } // Ignore
            }

            // Clear all event handlers to prevent memory leaks
            QuoteUpdated = null;

            // Clear all dictionaries
            _timerHandlers.Clear();
            _quotes.Clear();
            _rings.Clear();
            _ringGates.Clear();
            _subRefCount.Clear();
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
