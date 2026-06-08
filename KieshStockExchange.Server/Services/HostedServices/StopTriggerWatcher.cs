using System.Collections.Concurrent;
using System.Threading.Channels;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.MarketEngineServices.Interfaces;
using Microsoft.Extensions.Configuration;
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
/// §3.6 P2/P5. Watches live quotes and promotes armed (Pending) stop orders when the price crosses
/// their trigger. A <b>static</b> stop has a fixed trigger; a <b>trailing</b> stop (§P5) derives its
/// trigger each tick from a monotonic watermark (best price since arm) and a fixed offset.
///
/// Threading: <see cref="IMarketDataService.QuoteUpdated"/> is raised from a single QuoteRegistry drain
/// loop, so <see cref="OnQuoteUpdated"/> is serialized across all stocks on one thread — the watermark is
/// therefore single-writer and the per-tick path is lock-free. Persistence of moving watermarks is
/// throttled and batched off that thread (see the flush loop) so a quote storm does zero per-tick DB
/// writes. The watermark is monotonic, so a stale persisted value can only make a restored stop
/// <i>looser</i> (further from price) — never tighter — so a restart can never cause an early fire.
///
/// Promotion is unchanged: the quote thread does an atomic <c>TryRemove</c> (double-trigger guard) and
/// enqueues; a single drain loop runs <see cref="IOrderExecutionService.PromoteStopAsync"/> (book →
/// user-gate → tx). The watcher never touches the book.
/// </summary>
public sealed class StopTriggerWatcher : BackgroundService, IStopWatcher
{
    // Mutable so the quote thread can ratchet a trailing watermark in place (single-writer).
    private sealed class WatchedStop
    {
        public int OrderId;
        public int StockId;
        public CurrencyType Ccy;
        public bool IsBuy;
        // Static stops: fixed trigger. Trailing: last computed effective trigger (recomputed each tick).
        public decimal StopPrice;
        // Trailing (immutable after Arm except Watermark / dirty bookkeeping, all quote-thread-only):
        public bool IsTrailing;
        public decimal Offset;
        public bool IsPercent;
        public decimal Watermark;
        public bool WatermarkSeeded;
        public decimal LastDirtyWatermark;   // last value published to the flusher (quote-thread only)
    }

    // Fired payload: carries the realized trigger + watermark so the drain loop can persist them.
    private readonly record struct FiredStop(int OrderId, decimal EffectiveStop, decimal Watermark, bool IsTrailing);

    private const decimal MinDirtyAbs = 0.01m;          // floor so tiny ticks never thrash the dirty-set
    private const decimal DirtyFraction = 0.10m;        // publish when watermark moves ≥ 10% of trail distance
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    // §P6 stop-promotion circuit breaker: cap how many stops a single (stock,currency) can PROMOTE per
    // short window. Excess fires are deferred (never dropped) and re-fed in the next window, so a chain
    // reaction can't fire dozens of stops at once even when each is slippage-capped — the value anchor +
    // far walls absorb the throttled trickle. Default cap/window only bite during an actual cascade.
    private sealed class PromoWindow { public DateTime Start; public int Count; }
    private readonly Dictionary<(int StockId, CurrencyType Ccy), PromoWindow> _promoBuckets = new();
    private readonly List<FiredStop> _deferred = new();
    private readonly object _breakerGate = new();
    private readonly int _breakerCap;
    private readonly TimeSpan _breakerWindow;

    // (stock,ccy) -> orderId -> watched stop.
    private readonly ConcurrentDictionary<(int StockId, CurrencyType Ccy), ConcurrentDictionary<int, WatchedStop>> _index = new();
    // orderId -> bucket key, so Disarm is O(1) without scanning every bucket.
    private readonly ConcurrentDictionary<int, (int StockId, CurrencyType Ccy)> _byOrderId = new();
    // Trailing watermarks pending persist: orderId -> published watermark snapshot (safe publication;
    // the flusher reads this snapshot, never the live mutable field — no torn decimal read).
    private readonly ConcurrentDictionary<int, decimal> _dirtyWatermarks = new();
    private readonly Channel<FiredStop> _toPromote =
        Channel.CreateUnbounded<FiredStop>(new UnboundedChannelOptions { SingleReader = true });

    private readonly IMarketDataService _market;
    private readonly IOrderExecutionService _engine;
    private readonly IOrderRegistry _registry;
    private readonly IDataBaseService _db;
    private readonly ILogger<StopTriggerWatcher> _logger;

    public StopTriggerWatcher(IMarketDataService market, IOrderExecutionService engine,
        IOrderRegistry registry, IDataBaseService db, ILogger<StopTriggerWatcher> logger,
        IConfiguration config)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _breakerCap = Math.Max(1, config?.GetValue("Bots:StopBreaker:MaxPromotionsPerWindow", 3) ?? 3);
        _breakerWindow = TimeSpan.FromSeconds(
            Math.Max(1.0, config?.GetValue("Bots:StopBreaker:WindowSeconds", 10.0) ?? 10.0));
    }

    #region IStopWatcher
    public void Arm(Order o)
    {
        if (o is null || !o.IsStopOrder || o.OrderId <= 0) return;

        var ws = new WatchedStop
        {
            OrderId = o.OrderId, StockId = o.StockId, Ccy = o.CurrencyType, IsBuy = o.IsBuyOrder,
            StopPrice = o.StopPrice ?? 0m,
        };

        if (o.Stop == StopKind.Trailing)
        {
            // A trailing stop without a positive offset can't fire sanely — drop it (validator guards
            // the place path; this is belt-and-suspenders for a malformed cold-load row).
            if (!o.TrailOffset.HasValue || o.TrailOffset.Value <= 0m) return;
            ws.IsTrailing = true;
            ws.Offset = o.TrailOffset.Value;
            ws.IsPercent = o.TrailIsPercent ?? false;
            if (o.TrailWatermark is decimal wm && wm > 0m)
            {
                ws.Watermark = wm;
                ws.LastDirtyWatermark = wm;
                ws.WatermarkSeeded = true;
                ws.StopPrice = TrailMath.EffectiveStop(wm, ws.Offset, ws.IsPercent, ws.IsBuy);
            }
            // else: seed on the first quote (handles a cold-load row with no persisted watermark).
        }
        else if (ws.StopPrice <= 0m)
        {
            return; // a static stop needs a trigger
        }

        var key = (o.StockId, o.CurrencyType);
        var bucket = _index.GetOrAdd(key, static _ => new ConcurrentDictionary<int, WatchedStop>());
        bucket[o.OrderId] = ws;
        _byOrderId[o.OrderId] = key;
    }

    public void Disarm(int orderId)
    {
        if (_byOrderId.TryRemove(orderId, out var key) && _index.TryGetValue(key, out var bucket))
            bucket.TryRemove(orderId, out _);
        _dirtyWatermarks.TryRemove(orderId, out _);
    }
    #endregion

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ColdLoadAsync(stoppingToken).ConfigureAwait(false);
        _market.QuoteUpdated += OnQuoteUpdated;
        var flushTask = FlushLoopAsync(stoppingToken);
        var refeedTask = BreakerRefeedLoopAsync(stoppingToken);
        try
        {
            await foreach (var fired in _toPromote.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // §P6 circuit breaker: throttle promotions per (stock,window). Over budget → defer this
                // fire (re-fed next window); never dropped, so the armed stop is never orphaned.
                if (!TryAdmitPromotion(fired.OrderId)) { Defer(fired); continue; }
                try
                {
                    // Persist the realized trigger on the canonical instance so PromoteStopAsync's
                    // post-promote UpdateOrder records it (history/chart). The fired stop was already
                    // removed from the watcher index, so the quote thread won't touch it concurrently.
                    if (fired.IsTrailing && _registry.TryGet(fired.OrderId, out var canon))
                    {
                        canon.StopPrice = CurrencyHelper.RoundMoney(fired.EffectiveStop, canon.CurrencyType);
                        canon.TrailWatermark = fired.Watermark;
                    }
                    await _engine.PromoteStopAsync(fired.OrderId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stop promotion failed for order #{OrderId}.", fired.OrderId);
                }
            }
        }
        finally
        {
            _market.QuoteUpdated -= OnQuoteUpdated;
            try { await flushTask.ConfigureAwait(false); } catch { /* shutdown */ }
            try { await refeedTask.ConfigureAwait(false); } catch { /* shutdown */ }
        }
    }

    // Rebuild the armed index from DB on start so stops survive a restart. Trailing stops resume from
    // their persisted watermark (the §P5 staleness contract: at worst slightly looser, never earlier).
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
            decimal effStop;

            if (s.IsTrailing)
            {
                if (!s.WatermarkSeeded)
                {
                    s.Watermark = price;
                    s.LastDirtyWatermark = price;
                    s.WatermarkSeeded = true;
                    _dirtyWatermarks[s.OrderId] = price;   // persist the seed
                }
                else
                {
                    var ratcheted = TrailMath.Ratchet(s.Watermark, price, s.IsBuy);
                    if (ratcheted != s.Watermark)
                    {
                        s.Watermark = ratcheted;
                        MaybeMarkDirty(s);
                    }
                }
                effStop = TrailMath.EffectiveStop(s.Watermark, s.Offset, s.IsPercent, s.IsBuy);
                s.StopPrice = effStop; // cache the live effective trigger (single-writer)
            }
            else
            {
                effStop = s.StopPrice;
            }

            if (!TrailMath.Crossed(price, effStop, s.IsBuy)) continue;

            // Atomic remove = double-trigger guard: only the thread that removes promotes.
            if (bucket.TryRemove(s.OrderId, out _))
            {
                _byOrderId.TryRemove(s.OrderId, out _);
                _dirtyWatermarks.TryRemove(s.OrderId, out _);
                _toPromote.Writer.TryWrite(new FiredStop(s.OrderId, effStop, s.Watermark, s.IsTrailing));
            }
        }
    }

    // Publish the watermark to the flusher only after it advances past a threshold since the last
    // publish — trivial ticks never mark dirty, bounding DB writes regardless of quote rate.
    private void MaybeMarkDirty(WatchedStop s)
    {
        decimal dist = TrailMath.Distance(s.Watermark, s.Offset, s.IsPercent);
        decimal threshold = Math.Max(MinDirtyAbs, DirtyFraction * dist);
        if (Math.Abs(s.Watermark - s.LastDirtyWatermark) >= threshold)
        {
            s.LastDirtyWatermark = s.Watermark;
            _dirtyWatermarks[s.OrderId] = s.Watermark;   // last-writer-wins snapshot for the flusher
        }
    }

    // Off-thread, low-frequency: drain the dirty-set into one batched watermark/trigger UPDATE.
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(FlushInterval);
            using var cancelReg = ct.Register(static state => ((PeriodicTimer)state!).Dispose(), timer);
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                await FlushDirtyAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogError(ex, "Trailing-stop flush loop terminated unexpectedly."); }
        finally
        {
            // Best-effort final drain so the last window's watermarks aren't lost on a clean shutdown.
            try { await FlushDirtyAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task FlushDirtyAsync(CancellationToken ct)
    {
        if (_dirtyWatermarks.IsEmpty) return;
        var batch = new List<(int OrderId, decimal Watermark, decimal StopPrice)>(_dirtyWatermarks.Count);
        foreach (var kv in _dirtyWatermarks)
        {
            if (!_dirtyWatermarks.TryRemove(kv.Key, out var wm)) continue;
            // Resolve the (immutable) trail params to recompute the effective trigger from the published
            // watermark snapshot. If the stop fired/disarmed since being marked, it's gone from the index
            // — skip it (its terminal state is persisted by the promote/cancel path).
            if (_byOrderId.TryGetValue(kv.Key, out var key)
                && _index.TryGetValue(key, out var bucket)
                && bucket.TryGetValue(kv.Key, out var s) && s.IsTrailing)
            {
                batch.Add((kv.Key, wm, TrailMath.EffectiveStop(wm, s.Offset, s.IsPercent, s.IsBuy)));
            }
        }
        if (batch.Count > 0)
            await _db.UpdateTrailStateAsync(batch, ct).ConfigureAwait(false);
    }

    // §P6 circuit breaker: admit a promotion only if this (stock,currency) is under its per-window cap.
    // Single-threaded drain loop calls this, but the refeed loop also touches the buckets, so it's locked.
    private bool TryAdmitPromotion(int orderId)
    {
        // Can't resolve the order's stock (shouldn't happen for an armed stop) — don't lose it: promote now.
        if (!_registry.TryGet(orderId, out var o)) return true;
        var key = (o.StockId, o.CurrencyType);
        lock (_breakerGate)
        {
            var now = DateTime.UtcNow;
            if (!_promoBuckets.TryGetValue(key, out var w))
            {
                w = new PromoWindow { Start = now, Count = 0 };
                _promoBuckets[key] = w;
            }
            if (now - w.Start >= _breakerWindow) { w.Start = now; w.Count = 0; }
            if (w.Count >= _breakerCap) return false;
            w.Count++;
            return true;
        }
    }

    private void Defer(FiredStop fired)
    {
        lock (_breakerGate) _deferred.Add(fired);
    }

    // Once per window, push any deferred fires back into the promote channel; the drain loop re-checks
    // each against the (now rolled) per-stock budget. Drains a throttled trickle of cap/window/stock.
    private async Task BreakerRefeedLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_breakerWindow);
            using var cancelReg = ct.Register(static state => ((PeriodicTimer)state!).Dispose(), timer);
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                FiredStop[] batch;
                lock (_breakerGate)
                {
                    if (_deferred.Count == 0) continue;
                    batch = _deferred.ToArray();
                    _deferred.Clear();
                }
                foreach (var f in batch) _toPromote.Writer.TryWrite(f);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogError(ex, "Stop-breaker refeed loop terminated unexpectedly."); }
    }
}
