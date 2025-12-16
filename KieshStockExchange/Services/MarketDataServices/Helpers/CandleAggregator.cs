using KieshStockExchange.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed class CandleAggregator 
{
    #region Key and Configuration
    // If true, any gaps between candles will be filled with flat candles at last.Close
    public bool FillGapsEnabled;
    // Max number of gap candles to fill in one go
    private int MaxGapCandles = 10;

    // Key uniquely identifies this aggregator
    public (int StockId, CurrencyType Currency, int BucketSec) Key => (StockId, Currency, BucketSec);
    public readonly int StockId;
    public readonly CurrencyType Currency;
    public readonly int BucketSec;
    public readonly CandleResolution Resolution;
    public readonly TimeSpan Bucket;
    public string KeyString => $"{StockId}-{Currency.ToString()}-{BucketSec}s";
    #endregion

    #region Candle State
    // Current in-progress candle
    public Candle? LiveCandle { get; private set; } = null;
    public bool HasLive => LiveCandle is not null;
    public DateTime? LiveCandleStart => LiveCandle?.OpenTime;
    public DateTime? LiveCandleEnd => LiveCandle?.CloseTime;

    // Closed candle tracking
    private DateTime? LastCloseTime = null;
    private decimal? LastClosedPrice = null;

    private readonly ConcurrentQueue<Candle> ClosedCandles = new();

    // Seen HashSet
    public readonly HashSet<int> SeenTxIds = new();
    #endregion

    #region Fields and Constructor
    private readonly ILogger _log;
    private readonly object _gate = new();

    public CandleAggregator(int stockId, CurrencyType currency, CandleResolution resolution, 
        ILogger log, bool fillGapsEnabled = true)
    {
        if (stockId <= 0) 
            throw new ArgumentOutOfRangeException(nameof(stockId));
        if (!CurrencyHelper.IsSupported(currency)) 
            throw new ArgumentOutOfRangeException(nameof(currency), "Unsupported currency.");
        // Key properties
        StockId = stockId; 
        Currency = currency; 
        Resolution = resolution;
        BucketSec = (int)resolution;  
        Bucket = TimeSpan.FromSeconds(BucketSec);
        FillGapsEnabled = fillGapsEnabled;
        // Dependencies
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }
    #endregion

    #region OnTick, Flush, Snapshot and Drain
    /// <summary> Apply one executed trade into this bucket's candle. </summary>
    public void OnTick(Transaction tick)
    {
        if (!tick.IsValid() || tick.TransactionId <= 0) 
            throw new ArgumentException("Invalid tick.");


        // Align tick time to bucket start
        var start = TimeHelper.FloorToBucketUtc(tick.Timestamp, Bucket);

        // Lock and update live candle, closing it if needed
        List<Candle> finished = new();
        lock (_gate)
        {
            // Start a new live candle if none
            if (!HasLive)
            {
                // Get the last closed price, or use this price if none
                var lastClose = LastClosedPrice ?? tick.Price;
                // If gap filling enabled, fill from last closed candle
                if (FillGapsEnabled && LastCloseTime.HasValue && start > LastCloseTime.Value)
                    finished.AddRange(FillGaps(LastCloseTime.Value, start, lastClose));
                // Set new live Candle and clear transaction ids
                LiveCandle = NewCandle(start, lastClose);
                SeenTxIds.Clear();
            }
            // If tick is before current live candle, ignore it
            else if (start < LiveCandleStart)
            {
                _log.LogWarning("Ignoring out-of-order tick for {Key} at {Time:u}", KeyString, tick.Timestamp);
                return;
            }
            // New bucket started
            else if (start > LiveCandleStart)
            {
                // Optionally fill any gaps with flat candles at last.Close
                if (FillGapsEnabled)
                    finished.AddRange(FillGaps(LiveCandle!.CloseTime, start, LiveCandle.Close));

                // Bucket has moved on, close current candle
                finished.Add(LiveCandle!);
                LastClosedPrice = LiveCandle!.Close;
                LastCloseTime = LiveCandle!.CloseTime;

                // Start new candle and clear transaction ids
                LiveCandle = NewCandle(start, tick.Price);
                SeenTxIds.Clear();
            }
            // Drop duplicate
            if (SeenTxIds.Contains(tick.TransactionId)) return;
            SeenTxIds.Add(tick.TransactionId);

            // Apply this trade into the current live candle
            LiveCandle!.ApplyTrade(tick);
        }

        foreach (var c in finished)
            ClosedCandles.Enqueue(c);
    }

    /// <summary> 
    /// Flushes the current live candle for persistence. 
    /// This closes the live candle if its close time has elapsed. 
    /// </summary>
    public void FlushIfElapsed(DateTime nowUtc)
    {
        Candle? finished = null;
        lock (_gate)
        {
            // If no live candle or still in its time, do nothing
            if (!HasLive || LiveCandle!.CloseTime > nowUtc)
                return;

            finished = LiveCandle!;
            LastClosedPrice = LiveCandle!.Close;
            LastCloseTime = LiveCandle!.CloseTime;
            LiveCandle = null; // mark closed
        }
        if (finished is not null)
            ClosedCandles.Enqueue(finished);
    }

    /// <summary> Return a copy of the current live candle, or null if none </summary>
    public Candle? TryGetLiveSnapshot()
    {
        lock (_gate)
            return LiveCandle?.Clone();
    }

    public List<Candle> DrainClosedCandles()
    {
        var list = new List<Candle>();
        while (ClosedCandles.TryDequeue(out var c))
            list.Add(c);
        return list;
    }
    #endregion

    #region Helpers
    private IEnumerable<Candle> FillGaps(DateTime from, DateTime toStart, decimal flat)
    {
        if (from >= toStart) yield break;
        
        var cur = from;
        int count = 0;

        // Walk forward from last.OpenTime to nextStart
        while (cur < toStart && count++ < MaxGapCandles)
        {
            yield return NewCandle(cur, flat);
            cur += Bucket;
        }

        // Warn if too many gaps
        if (cur < toStart)
            _log.LogWarning("Too many gaps to fill for {Key}, stopped at {Time:u}", KeyString, cur);
    }

    private Candle NewCandle(DateTime openTime, decimal price) =>  new() 
    {
        StockId = StockId, CurrencyType = Currency,
        BucketSeconds = BucketSec, OpenTime = openTime,
        Open = price, Close = price,
        High = price, Low = price,
        Volume = 0, TradeCount = 0,
    };
    #endregion
}


