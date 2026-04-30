using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.MarketDataServices;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.Implementations;

/// <summary>
/// Thin facade that composes <see cref="QuoteRegistry"/>, <see cref="SubscriptionTracker"/>,
/// and <see cref="TickPipeline"/> behind the <see cref="IMarketDataService"/> interface.
/// All state lives in the components; this class just wires them together, forwards
/// the <see cref="QuoteUpdated"/> event, orchestrates candle subscribe/unsubscribe on
/// first/last subscriber, and owns the lifecycle.
/// </summary>
public sealed class MarketDataService : IMarketDataService, IAsyncDisposable
{
    private readonly QuoteRegistry _registry;
    private readonly SubscriptionTracker _subs;
    private readonly TickPipeline _pipeline;

    private readonly ILogger<MarketDataService> _logger;
    private readonly ICandleService _candle;
    private readonly IMarketLookupService _lookup;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public IReadOnlyDictionary<(int stockId, CurrencyType currency), LiveQuote> Quotes => _registry.Quotes;

    public IReadOnlyCollection<(int, CurrencyType)> Subscribed => _subs.Subscribed;

    public event EventHandler<LiveQuote>? QuoteUpdated;

    public MarketDataService( IDispatcher dispatcher, ILogger<MarketDataService> logger,
        ILoggerFactory loggerFactory, ICandleService candle, IMarketLookupService lookup)
    {
        if (dispatcher is null) throw new ArgumentNullException(nameof(dispatcher));
        if (loggerFactory is null) throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _candle = candle ?? throw new ArgumentNullException(nameof(candle));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));

        _subs = new SubscriptionTracker();
        _registry = new QuoteRegistry(
            dispatcher, lookup,
            loggerFactory.CreateLogger<QuoteRegistry>(),
            _subs.HasUiSubscribers);
        _pipeline = new TickPipeline(
            _registry, _subs, candle, dispatcher, lookup,
            loggerFactory.CreateLogger<TickPipeline>());

        _registry.QuoteUpdated += OnRegistryQuoteUpdated;
        _registry.Start(_lifetimeCts.Token);
        _pipeline.Start();
    }

    private void OnRegistryQuoteUpdated(object? sender, LiveQuote q)
        => QuoteUpdated?.Invoke(this, q);

    #region Session
    public Task ApplySessionSnapshotAsync(SessionSnapshot snap)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var newRes = snap.DefaultCandleResolution;
                var oldRes = _subs.CurrentResolution;
                if (newRes == oldRes) return;

                _subs.CurrentResolution = newRes;

                foreach (var (stockId, currency) in _subs.SnapshotSubscribed())
                {
                    try
                    {
                        await _candle.UnsubscribeAsync(stockId, currency, oldRes).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error unsubscribing candles for {StockId} {Currency} at {Resolution}",
                            stockId, currency, oldRes);
                    }

                    if (_subs.HasAnySubscribers((stockId, currency)))
                    {
                        try { _candle.Subscribe(stockId, currency, newRes); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "Error subscribing candles for {StockId} {Currency} at {Resolution}",
                                stockId, currency, newRes);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying session snapshot changes.");
            }
        });

        return Task.CompletedTask;
    }
    #endregion

    #region Subscriptions
    public Task SubscribeAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
        => SubscribeAsync(stockId, currency, forUi: true, ct);

    public Task Unsubscribe(int stockId, CurrencyType currency, CancellationToken ct = default)
        => Unsubscribe(stockId, currency, forUi: true, ct);

    public Task SubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
        => SubscribeAllAsync(currency, forUi: true, ct);

    public Task UnsubscribeAllAsync(CurrencyType currency, CancellationToken ct = default)
        => UnsubscribeAllAsync(currency, forUi: true, ct);

    public async Task SubscribeAsync(int stockId, CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        var key = (stockId, currency);
        // Bump counts first so GetOrAddAsync's hasUiSubscriber check sees the new
        // subscriber and uses the UI thread when needed.
        var (_, first) = _subs.Increment(key, forUi);
        await _registry.GetOrAddAsync(stockId, currency, ct).ConfigureAwait(false);
        if (first)
            _candle.Subscribe(stockId, currency, _subs.CurrentResolution);
    }

    public async Task Unsubscribe(int stockId, CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        var key = (stockId, currency);
        var (_, last) = _subs.Decrement(key, forUi);
        if (last)
        {
            _registry.TryRemove(key);
            await _candle.UnsubscribeAsync(stockId, currency, _subs.CurrentResolution, ct).ConfigureAwait(false);
        }
    }

    public async Task SubscribeAllAsync(CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        // Load the catalog once; per-call SubscribeAsync would otherwise hit
        // EnsureLoadedAsync inside GetStockAsync N times.
        var stocks = await _lookup.GetAllStocksAsync(ct).ConfigureAwait(false);
        for (int i = 0; i < stocks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await SubscribeAsync(stocks[i].StockId, currency, forUi, ct).ConfigureAwait(false);
        }
    }

    public async Task UnsubscribeAllAsync(CurrencyType currency, bool forUi, CancellationToken ct = default)
    {
        var stocks = await _lookup.GetAllStocksAsync(ct).ConfigureAwait(false);
        for (int i = 0; i < stocks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Unsubscribe(stocks[i].StockId, currency, forUi, ct).ConfigureAwait(false);
        }
    }
    #endregion

    #region Tick handling and history seeding
    public Task OnTick(Transaction tick, CancellationToken ct = default)
        => _pipeline.OnTick(tick, ct);

    public Task OnTicksAsync(IReadOnlyList<Transaction> ticks, CancellationToken ct = default)
        => _pipeline.OnTicksAsync(ticks, ct);

    public Task BuildFromHistoryAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
        => _pipeline.BuildFromHistoryAsync(stockId, currency, ct);
    #endregion

    #region Convenience lookups
    public async Task<decimal> GetLastPriceAsync(int stockId, CurrencyType currency, CancellationToken ct = default)
    {
        // Snapshot read against the live registry — never inserts an empty LiveQuote
        // for lookup-only callers (admin VMs, snapshot service, decision service).
        if (_registry.Quotes.TryGetValue((stockId, currency), out var quote) && quote.LastPrice > 0m)
            return quote.LastPrice;

        var fromStore = await _lookup.GetLatestPriceFromStoreAsync(stockId, currency, ct).ConfigureAwait(false);
        if (fromStore is decimal price && price > 0m) return price;

        return 100m; // Default seed price
    }

    public Task<Stock?> GetStockAsync(int stockId, CancellationToken ct = default)
        => _lookup.GetStockAsync(stockId, ct);

    public Task<IReadOnlyList<Stock>> GetAllStocksAsync(CancellationToken ct = default)
        => _lookup.GetAllStocksAsync(ct);

    public Task<decimal> GetDateTimePriceAsync(int stockId, CurrencyType currency, DateTime time, CancellationToken ct = default)
        => _lookup.GetDateTimePriceAsync(stockId, currency, time, ct);
    #endregion

    #region IAsyncDisposable
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Stop accepting new ticks and let the reader + drain loop drain.
            _lifetimeCts.Cancel();
            await _pipeline.StopAsync().ConfigureAwait(false);
            await _registry.StopAsync().ConfigureAwait(false);

            // Tear down candle streams for any books still tracked.
            foreach (var (stockId, currency) in _subs.SnapshotSubscribed())
            {
                try { await _candle.UnsubscribeAsync(stockId, currency, _subs.CurrentResolution).ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            _registry.QuoteUpdated -= OnRegistryQuoteUpdated;
            QuoteUpdated = null;
            _registry.Clear();
            _lifetimeCts.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing MarketDataService.");
        }
    }
    #endregion
}
