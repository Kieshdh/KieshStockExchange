using CommunityToolkit.Mvvm.ComponentModel;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;

namespace KieshStockExchange.Services.Implementations;

public partial class MarketDataService : ObservableObject, IMarketDataService
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

    #region Live Quotes Dictionaries and subscriptions
    private readonly ConcurrentDictionary<(int, CurrencyType), LiveQuote> _quotes = new();
    private readonly ReadOnlyDictionary<(int, CurrencyType), LiveQuote> _quotesView;
    public IReadOnlyDictionary<(int, CurrencyType), LiveQuote> Quotes => _quotesView;

    public event EventHandler<LiveQuote>? QuoteUpdated;

    private readonly ConcurrentDictionary<(int, CurrencyType), int> _subRefCount = new();
    private readonly ConcurrentDictionary<(int, CurrencyType), IDispatcherTimer> _timers = new();

    public IReadOnlyCollection<(int, CurrencyType)> Subscribed => 
        _subRefCount.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList().AsReadOnly();

    private static readonly TimeSpan TickerInterval = TimeSpan.FromMilliseconds(250);
    #endregion

    #region Internal ring buffer
    // ring buffer per stock for simple rolling metrics (prices + timestamps)
    private sealed class Ring
    {
        private readonly object _gate = new();

        public readonly decimal[] Prices;
        public readonly DateTime[] Times;
        public int Head = 0;
        public int Count = 0;

        public Ring(int capacity)
        {
            Prices = new decimal[capacity];
            Times = new DateTime[capacity];
        }

        public void Add(decimal price, DateTime time)
        {
            lock (_gate)
            {
                Prices[Head] = price;
                Times[Head] = time;
                Head = (Head + 1) % Prices.Length;
                Count = Math.Min(Count + 1, Prices.Length);
            }
        }

        public (decimal price, DateTime time)? TryPeekNewest()
        {
            lock (_gate)
            {
                if (Count == 0) return null;
                int idx = (Head - 1 + Prices.Length) % Prices.Length;
                return (Prices[idx], Times[idx]);
            }
        }

        public void SortByTime() => Array.Sort(Times, Prices, 0, Count);

        public IEnumerable<(decimal p, DateTime t)> EnumerateNewestFirst()
        {
            for (int i = 0; i < Count; i++)
            {
                int idx = (Head - 1 - i + Prices.Length) % Prices.Length;
                yield return (Prices[idx], Times[idx]);
            }
        }
    }

    private readonly ConcurrentDictionary<(int, CurrencyType), Ring> _rings = new();

    private static readonly TimeSpan StoreDuration = TimeSpan.FromMinutes(5);
    private static int RingCapacity => 
        (int)(StoreDuration.TotalMilliseconds / TickerInterval.TotalMilliseconds);
    #endregion

    #region Stock details
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

    public async Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        var stocks = await _db.GetStocksAsync(ct);
        foreach (var stock in stocks)
            _stockCache[stock.StockId] = stock;
        return stocks.AsReadOnly();
    }

    public async Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        var quote = await GetOrAddQuote(stockId, currency, ct);
        return quote.LastPrice;
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
        var count = _subRefCount.AddOrUpdate(key, 0, (_, c) => Math.Max(0, c - 1));
        if (count == 0)
        {
            _quotes.TryRemove(key, out _);
            _rings.TryRemove(key, out _);
            if (_timers.TryRemove(key, out var timer))
                _dispatcher.Dispatch(() => timer.Stop());
        }
    }

    public async Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
    {
        var Stocks = await GetAllStocksAsync(ct);
        foreach (var stock in Stocks)
            await SubscribeAsync(stock.StockId, currency, ct);
    }
    #endregion

    #region Build LiveQuote from ticks
    public async Task OnTick(Transaction tick)
    {
        if (!tick.IsValid())
        {
            _logger.LogWarning("Invalid tick received: {@Tick}", tick);
            return;
        }

        var stockId = tick.StockId;
        var currency = tick.CurrencyType;

        // Update live quote
        var quote = await GetOrAddQuote(stockId, currency);
        var utc = TimeHelper.EnsureUtc(tick.Timestamp);

        // Keep ring for rolling stats
        var ring = GetRing(stockId, currency);
        ring.Add(tick.Price, utc);

        // Marshal property changes to UI thread
        if (_subRefCount.TryGetValue((stockId, currency), out var c) && c > 0)
        {
            // Debounce rapid updates to avoid UI flooding
            var timer = _timers.GetOrAdd((stockId, currency), _ => {
                var t = _dispatcher.CreateTimer();
                t.Interval = TickerInterval;
                t.IsRepeating = false;
                t.Tick += (_, __) => {
                    if (_quotes.TryGetValue((stockId, currency), out var q)) 
                        QuoteUpdated?.Invoke(this, q);
                };
                return t;
            });
            // Dispatch the update
            _dispatcher.Dispatch(() => {
                quote.ApplyTick(tick.Price, tick.Quantity, utc);
                if (timer.IsRunning) timer.Stop();
                timer.Start();
            });
        }
            
    }

    public async Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Get the quote and ring
        var quote = await GetOrAddQuote(stockId, currency, ct);
        var ring = GetRing(stockId, currency);

        // If we already have data, skip
        if (ring.Count > 0)
            return;

        // Load last 24 hours of historical prices
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
        foreach (var tick in history)
        {
            var dt = TimeHelper.EnsureUtc(tick.Timestamp);
            ring.Add(tick.Price, dt);
            if (tick.Price > high) high = tick.Price;
            if (tick.Price < low) low = tick.Price;
        }

        _dispatcher.Dispatch(() =>
        {
            var LastUtc = TimeHelper.EnsureUtc(history[^1].Timestamp);
            var volume = history.Sum(t => t.Quantity);
            var last = history[^1].Price;

            // Apply data to quote
            quote.SessionStartUtc = LastUtc.Date;
            quote.Open = open;
            quote.High = high;
            quote.Low = low;
            quote.ApplyTick(last, volume, LastUtc);

            // Notify listeners
            QuoteUpdated?.Invoke(this, quote);
        });
    }

    public async IAsyncEnumerable<Candle> StreamCandlesAsync(
        int stockId, CurrencyType currency, TimeSpan bucket, bool fillGaps, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Simple aggregator: compute OHLC for each completed bucket.
        DateTime? bucketStart = null;
        decimal o = 0, h = 0, l = 0, c = 0;

        while (!ct.IsCancellationRequested)
        {
            // Poll the latest ring entry and aggregate
            if (_rings.TryGetValue((stockId, currency), out var ring) && ring.Count > 0)
            {
                var peek = ring.TryPeekNewest();
                if (peek is { } value)
                {
                    var price = value.price;
                    var time = value.time;
                    var start = Align(time, bucket);
                    if (bucketStart == null) 
                    { 
                        bucketStart = start; 
                        o = h = l = c = price; 
                    }
                    if (start == bucketStart) 
                    { 
                        c = price; 
                        h = Math.Max(h, price); 
                        l = Math.Min(l, price); 
                    }
                    else
                    {
                        // emit the finished candle
                        yield return new Candle(stockId, bucketStart.Value, bucket, o, h, l, c);
                        // start new bucket
                        bucketStart = start; 
                        o = h = l = c = price;
                    }
                }
            }
            // Fill gaps with flat candles if requested
            if (fillGaps && bucketStart is not null)
            {
                // Use wall-clock to fill empty buckets with flat candles
                var nowAligned = Align(DateTime.UtcNow, bucket);
                while (nowAligned > bucketStart)
                {
                    yield return new Candle(stockId, bucketStart.Value, bucket, c, c, c, c);
                    bucketStart = bucketStart.Value + bucket;
                    o = h = l = c; // carry forward last close
                }
            }
            // Wait a bit before polling again
            await Task.Delay(TickerInterval, ct);
        }

        static DateTime Align(DateTime t, TimeSpan b) => 
            new(((t.Ticks / b.Ticks) * b.Ticks), DateTimeKind.Utc);
    }
    #endregion

    #region Private Helpers
    private async Task<LiveQuote> GetOrAddQuote(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Get or create the LiveQuote
        var quote = _quotes.GetOrAdd((stockId, currency), _ => new LiveQuote(stockId, currency));

        try
        {
            // If no price yet, try to build from history
            if (quote.LastPrice == 0m)
                await BuildFromHistoryAsync(stockId, currency, ct);

            // If still no symbol, fetch stock details
            if (quote.Symbol == "-")
            {
                var stock = await GetStockAsync(stockId, ct);
                if (stock != null)
                {
                    quote.Symbol = stock.Symbol;
                    quote.CompanyName = stock.CompanyName;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing LiveQuote for stock {StockId} in {Currency}", stockId, currency);
        }

        return quote;
    }

    private Ring GetRing(int stockId, CurrencyType currency)
        => _rings.GetOrAdd((stockId, currency), _ => new Ring(RingCapacity));

    private async Task<List<Transaction>> GetHistoricalTicksAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Load last day of historical transactions
        var history = await _db.GetTransactionsByStockIdAndTimeRange( stockId, currency, 
            DateTime.UtcNow - TimeSpan.FromDays(1), DateTime.UtcNow, ct);
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
}
