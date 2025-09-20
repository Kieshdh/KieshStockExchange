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
    private readonly ConcurrentDictionary<(int, CurrencyType), int> _subscribed = new();
    public IReadOnlyCollection<(int, CurrencyType)> Subscribed => _subscribed.Keys.ToList().AsReadOnly();
    #endregion

    #region Internal ring buffer
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

        public void Add(decimal price, DateTime time)
        {
            Prices[Head] = price;
            Times[Head] = time;
            Head = (Head + 1) % Prices.Length;
            Count = Math.Min(Count + 1, Prices.Length);
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
    #endregion

    #region Stock details
    private readonly ConcurrentDictionary<int, Stock> _stockCache = new();

    public async Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default)
    {
        if (_stockCache.TryGetValue(stockId, out var stock))
            return stock;
        stock = await _db.GetStockById(stockId, ct);
        if (stock != null)
            _stockCache[stockId] = stock;
        return stock;
    }

    public async Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
    {
        var stocks = await _db.GetStocksAsync(ct);
        foreach (var stock in stocks)
            _stockCache[stock.StockId] = stock;
        return stocks.AsReadOnly();
    }
    #endregion

    #region Subscribe/Unsubscribe
    public async Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        await GetOrAddQuote(stockId, currency, ct);
        _subRefCount.AddOrUpdate((stockId, currency), 1, (_, c) => c + 1);
        _subscribed[(stockId, currency)] = 0;
    }

    public void Unsubscribe(int stockId, CurrencyType currency)
    {
        if (_subRefCount.TryGetValue((stockId, currency), out var c))
        {
            if (c <= 1)
            {
                _subRefCount.TryRemove((stockId, currency), out _);
                _subscribed.TryRemove((stockId, currency), out _);
            }
            else
            {
                _subRefCount[(stockId, currency)] = c - 1;
            }
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
    public async Task OnTick(StockPrice tick)
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
        var utc = ToUtc(tick.Timestamp);

        // Keep ring for rolling stats
        var ring = _rings.GetOrAdd((stockId, currency), _ => new Ring(600));
        ring.Add(tick.Price, utc);

        // Marshal property changes to UI thread
        _dispatcher.Dispatch(() =>
        {
            quote.ApplyTick(tick.Price, 0, utc);
            QuoteUpdated?.Invoke(this, quote);
        });
    }

    public async IAsyncEnumerable<Candle> StreamCandlesAsync(
        int stockId, CurrencyType currency, TimeSpan bucket, [EnumeratorCancellation] CancellationToken ct = default)
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
            if (_rings.TryGetValue((stockId, currency), out var ring) && ring.Count > 0)
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
    #endregion

    #region Private Helpers
    private async Task<LiveQuote> GetOrAddQuote(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Get or create the LiveQuote
        var quote = _quotes.GetOrAdd((stockId, currency), _ => new LiveQuote(stockId, currency));
        _rings.GetOrAdd((stockId, currency), _ => new Ring(600));

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

    private async Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
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
        // Sort by time ascending
        history.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // Get the quote and ring
        var quote = await GetOrAddQuote(stockId, currency);
        var ring = _rings.GetOrAdd((stockId, currency), _ => new Ring(600));

        // Set initial OHLC from history
        decimal open = history[0].Price;
        decimal high = history[0].Price;
        decimal low = history[0].Price;
        decimal last = history[^1].Price;
        DateTime lastUtc = DateTime.MinValue;

        foreach (var tick in history)
        {
            var utc = ToUtc(tick.Timestamp);
            ring.Add(tick.Price, utc);
            if (tick.Price > high) high = tick.Price;
            if (tick.Price < low) low = tick.Price;
            lastUtc = utc;
        }

        _dispatcher.Dispatch(() =>
        {
            if (quote.Open <= 0m) quote.Open = open;
            quote.High = Math.Max(quote.High, high);
            quote.Low = quote.Low == 0m ? low : Math.Min(quote.Low, low);
            quote.ApplyTick(last, 0, lastUtc);
            QuoteUpdated?.Invoke(this, quote);
        });
    }

    private DateTime ToUtc(DateTime dt) =>
        dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
    #endregion
}
