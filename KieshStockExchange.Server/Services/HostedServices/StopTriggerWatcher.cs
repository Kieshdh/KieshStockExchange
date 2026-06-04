using System.Collections.Concurrent;
using System.Threading.Channels;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Server.Services.HostedServices;

/// <summary>Registry side of the stop watcher: OrderEntryService arms/disarms through this.</summary>
public interface IStopWatcher
{
    void Arm(Order armedStop);
    void Disarm(int orderId);
}

/// <summary>
/// §3.6 P2. Watches live quotes and promotes armed (Pending) stop orders when the price crosses
/// their StopPrice. Holds an in-memory index per (stock, currency) so the quote thread does a
/// lock-free lookup; the cross check uses an atomic <c>TryRemove</c> as the double-trigger guard
/// (only the thread that removes the stop enqueues it). Promotion runs on a single background
/// drain loop via <see cref="IOrderExecutionService.PromoteStopAsync"/>, which re-acquires the
/// book → user-gate → tx order — the watcher never touches the book directly.
/// </summary>
public sealed class StopTriggerWatcher : BackgroundService, IStopWatcher
{
    private sealed record ArmedStop(int OrderId, int StockId, CurrencyType Currency, decimal StopPrice, bool IsBuy);

    // (stock,ccy) -> orderId -> armed stop.
    private readonly ConcurrentDictionary<(int StockId, CurrencyType Ccy), ConcurrentDictionary<int, ArmedStop>> _index = new();
    // orderId -> bucket key, so Disarm is O(1) without scanning every bucket.
    private readonly ConcurrentDictionary<int, (int StockId, CurrencyType Ccy)> _byOrderId = new();
    private readonly Channel<int> _toPromote =
        Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IMarketDataService _market;
    private readonly IOrderExecutionService _engine;
    private readonly IDataBaseService _db;
    private readonly ILogger<StopTriggerWatcher> _logger;

    public StopTriggerWatcher(IMarketDataService market, IOrderExecutionService engine,
        IDataBaseService db, ILogger<StopTriggerWatcher> logger)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region IStopWatcher
    public void Arm(Order o)
    {
        if (o is null || !o.IsStopOrder || !o.StopPrice.HasValue || o.OrderId <= 0) return;
        var key = (o.StockId, o.CurrencyType);
        var bucket = _index.GetOrAdd(key, static _ => new ConcurrentDictionary<int, ArmedStop>());
        bucket[o.OrderId] = new ArmedStop(o.OrderId, o.StockId, o.CurrencyType, o.StopPrice.Value, o.IsBuyOrder);
        _byOrderId[o.OrderId] = key;
    }

    public void Disarm(int orderId)
    {
        if (_byOrderId.TryRemove(orderId, out var key) && _index.TryGetValue(key, out var bucket))
            bucket.TryRemove(orderId, out _);
    }
    #endregion

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ColdLoadAsync(stoppingToken).ConfigureAwait(false);
        _market.QuoteUpdated += OnQuoteUpdated;
        try
        {
            await foreach (var orderId in _toPromote.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _engine.PromoteStopAsync(orderId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stop promotion failed for order #{OrderId}.", orderId);
                }
            }
        }
        finally
        {
            _market.QuoteUpdated -= OnQuoteUpdated;
        }
    }

    // Rebuild the armed index from DB on start so stops survive a restart.
    private async Task ColdLoadAsync(CancellationToken ct)
    {
        try
        {
            var armed = await _db.GetAllArmedStopsAsync(ct).ConfigureAwait(false);
            for (int i = 0; i < armed.Count; i++) Arm(armed[i]);
            _logger.LogInformation("StopTriggerWatcher cold-loaded {Count} armed stop(s).", armed.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StopTriggerWatcher cold-load failed; armed stops will not fire until re-armed.");
        }
    }

    private void OnQuoteUpdated(object? sender, LiveQuote q)
    {
        if (q is null || q.LastPrice <= 0m) return;
        if (!_index.TryGetValue((q.StockId, q.Currency), out var bucket) || bucket.IsEmpty) return;

        var price = q.LastPrice;
        foreach (var kv in bucket)
        {
            var s = kv.Value;
            // Buy-stop fires when the price rises to the stop; sell-stop when it falls to it.
            var crossed = s.IsBuy ? price >= s.StopPrice : price <= s.StopPrice;
            if (!crossed) continue;

            // Atomic remove = double-trigger guard: only the thread that removes promotes.
            if (bucket.TryRemove(s.OrderId, out _))
            {
                _byOrderId.TryRemove(s.OrderId, out _);
                _toPromote.Writer.TryWrite(s.OrderId);
            }
        }
    }
}
