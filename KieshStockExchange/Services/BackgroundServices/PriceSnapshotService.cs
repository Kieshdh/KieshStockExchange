using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices;

public interface IPriceSnapshotService
{
    /// <summary>
    /// Start taking price snapshots at the given cadence. 
    /// Default is hourly if null.
    /// </summary>
    /// <param name="interval">Time between snapshots</param>
    Task Start(TimeSpan? interval = null);

    /// <summary> Stop taking price snapshots. </summary>
    void Stop();
}
public sealed class PriceSnapshotService : IPriceSnapshotService, IDisposable
{
    #region Fields, Properties and Constructor 
    // Dependencies
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<PriceSnapshotService> _logger;
    private readonly IDataBaseService _db;
    private readonly IMarketDataService _market;

    // Timer for scheduling snapshots
    private IDispatcherTimer? _timer;
    private CancellationTokenSource? _cts;
    private TimeSpan Interval = TimeSpan.FromHours(1); // Default 1 hour

    public PriceSnapshotService(ILogger<PriceSnapshotService> logger,
        IDispatcher dispatcher, IDataBaseService db, IMarketDataService market)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _market = market ?? throw new ArgumentNullException(nameof(market));
    }
    #endregion

    #region IPriceSnapshotService Implementation
    public async Task Start(TimeSpan? interval = null)
    {
        if (_timer != null)
        {
            if (!interval.HasValue || interval.Value == Interval)
                return; // already running with same interval
        }

        // Stop any existing timer
        Stop();

        _cts = new CancellationTokenSource();

        // Update interval if provided and valid
        if (interval.HasValue && interval > TimeSpan.Zero)
            Interval = interval.Value;

        // Wait until the start of the next hour to align snapshots
        var now = TimeHelper.NowUtc();
        var nextTick = TimeHelper.NextBucketBoundaryUtc(now, Interval);
        var delay = nextTick - now;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);

            await CreateSnapShot().ConfigureAwait(false);

            // Create a repeating UI timer
            _timer ??= _dispatcher.CreateTimer();
            _timer.IsRepeating = true;
            _timer.Interval = Interval;
            _timer.Tick += TickMethod;
            _timer.Start();
        }
        catch (TaskCanceledException) { return; }
        catch (Exception ex) { _logger.LogError(ex, "Failed to start aligned snapshots."); }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;

        if (_timer is null) return;

        _dispatcher.Dispatch(() =>
        {
            _timer.Stop();
            _timer.Tick -= TickMethod;
        });

        _timer = null;
    }

    public void Dispose() => Stop();
    #endregion

    #region Timer Tick Handler
    private async void TickMethod(object? sender, EventArgs e)
    {
        try { await CreateSnapShot().ConfigureAwait(false); }
        catch (Exception ex) {
            _logger.LogError(ex, "Unhandled error in PriceSnapshotService.TickMethod.");
        }
    }

    private async Task CreateSnapShot()
    {
        try
        {
            CancellationToken ct = _cts?.Token ?? CancellationToken.None;

            // Get the list of subscribed stocks (or all if none subscribed)
            var subs = await GetSubs().ConfigureAwait(false);

            // Determine the current time bucket
            var bucketStart = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), Interval);
            var bucketEnd = bucketStart.Add(Interval);

            foreach (var (stockId, currency) in subs)
            {
                ct.ThrowIfCancellationRequested();

                var price = await _market.GetLastPriceAsync(stockId, currency, ct).ConfigureAwait(false);
                if (price <= 0m) continue; // nothing to snapshot

                // Only write if we don’t already have a snapshot in this hour
                var existing = await _db.GetStockPricesByStockIdAndTimeRange(
                    stockId, currency, bucketStart, bucketEnd, ct).ConfigureAwait(false);

                // Create a new snapshot
                if (existing.Count == 0)
                    await CreateStockPrice(stockId, currency, price, ct).ConfigureAwait(false); // Create new snapshot
                else _logger.LogInformation("Snapshot already exists for StockId {StockId} in {Currency} " +
                    "between {Start} and {End}", stockId, currency, bucketStart, bucketEnd);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Price snapshot failed with interval {Interval}", Interval); }
    }

    private async Task<List<(int, CurrencyType)>> GetSubs()
    {
        var subs = _market.Subscribed.ToList(); // (stockId, currency) keys
        if (subs.Count == 0)
        {
            // Option B: fallback to all stocks if nothing is subscribed
            var stocks = await _market.GetAllStocksAsync().ConfigureAwait(false);
            subs = stocks.Select(s => (s.StockId, CurrencyType.USD)).ToList();
        }
        return subs;
    }

    private async Task CreateStockPrice(int stockId, CurrencyType currency, decimal price, CancellationToken ct)
    {
        try
        {
            var sp = new StockPrice
            {
                StockId = stockId, CurrencyType = currency, Price = price,
                Timestamp = TimeHelper.FloorNowToBucketUtc(Interval)
            };
            await _db.CreateStockPrice(sp, ct).ConfigureAwait(false);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to create stock price snapshot for StockId {StockId} in {Currency}", stockId, currency);
        }
    }
    #endregion
}
