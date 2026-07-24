using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.SignalR;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Phase 3 finish — client-side ICandleService backed by SignalR (live closes)
/// + HTTP (historical). The aggregator state stays server-side; this proxy
/// only relays closed candles to subscribers and serves history through the
/// CandleController.
///
/// Aggregators / OnTransactionTick / FixCandlesAsync / aggregation helpers
/// throw because no client consumer should call them after Phase 3 —
/// candle math is engine work.
/// </summary>
public sealed class SignalRCandleService : ICandleService, IAsyncDisposable
{
    private readonly IMarketHubClient _hub;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SignalRCandleService> _logger;

    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), int> _subRefs = new();
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), Channel<Candle>> _streams = new();

    // Last non-empty history result per key — served on a fetch FAULT so a
    // transient network blip never blanks a chart that was already populated
    // (candle-cache plan step 3, "serve-stale-on-fault"). Not a general cache:
    // a legitimately-empty response is passed through unchanged.
    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), IReadOnlyList<Candle>> _lastGood = new();

    // Client history cache (candle-cache plan steps 4-5): cache-first serve of fully-covered sealed ranges,
    // kept warm by live closes. Gated by CandleCache.Enabled (default off ⇒ populated-but-never-served ⇒
    // byte-identical). Owns both the HTTP fetch and the hub fan-out, so read + live-merge sit in one place.
    private readonly CandleCache _cache = new();

    public ConcurrentDictionary<(int, CurrencyType, CandleResolution), CandleAggregator> Aggregators =>
        throw new NotSupportedException("Candle aggregators live server-side after Phase 3.");

    public TimeSpan FlushInterval { get; } = TimeSpan.FromSeconds(1);

    public IReadOnlyCollection<(int, CurrencyType, CandleResolution)> Subscribed =>
        _subRefs.Keys.ToArray();

    public event EventHandler<Candle>? CandleClosed;

    public SignalRCandleService(IMarketHubClient hub, IHttpClientFactory httpFactory,
        ILogger<SignalRCandleService> logger)
    {
        _hub = hub;
        _httpFactory = httpFactory;
        _logger = logger;
        _hub.CandleClosed += OnHubCandleClosed;
    }

    private void OnHubCandleClosed(object? sender, Candle candle)
    {
        var resolution = (CandleResolution)candle.BucketSeconds;
        var key = (candle.StockId, candle.CurrencyType, resolution);

        // §client cache: fold every closed candle (all resolutions the hub delivers) into the cache so
        // backgrounded timeframes stay warm — this is the "subscription" of the cache plan (step 5). No-op
        // for uncached keys / when disabled.
        if (CandleCache.Enabled)
            _cache.MergeClosed(candle.StockId, candle.CurrencyType, resolution, candle);

        // Fan out to anyone streaming this key.
        if (_streams.TryGetValue(key, out var stream))
            stream.Writer.TryWrite(candle);

        try { CandleClosed?.Invoke(this, candle); }
        catch (Exception ex) { _logger.LogWarning(ex, "CandleClosed subscriber threw for {Summary}", candle.Summary); }
    }

    public void Subscribe(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        var key = (stockId, currency, resolution);
        var n = _subRefs.AddOrUpdate(key, 1, static (_, c) => c + 1);
        if (n == 1)
        {
            // First subscriber for this resolution — tell the server to start
            // aggregating. Without this, the engine's flush loop has no
            // aggregator for the key and CandleClosed never fires.
            _ = JoinHubSafelyAsync(stockId, currency, resolution);
        }
    }

    private async Task JoinHubSafelyAsync(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        try { await _hub.JoinCandlesAsync(stockId, currency, resolution).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "JoinCandles failed for {Stock}/{Currency}/{Res}", stockId, currency, resolution); }
    }

    public async Task UnsubscribeAsync(int stockId, CurrencyType currency, CandleResolution resolution, CancellationToken ct = default)
    {
        var key = (stockId, currency, resolution);
        var c = _subRefs.AddOrUpdate(key, 0, static (_, c) => Math.Max(0, c - 1));
        if (c == 0)
        {
            _subRefs.TryRemove(key, out _);
            if (_streams.TryRemove(key, out var stream))
                stream.Writer.TryComplete();
            try { await _hub.LeaveCandlesAsync(stockId, currency, resolution, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "LeaveCandles failed for {Stock}/{Currency}/{Res}", stockId, currency, resolution); }
        }
    }

    public Task SubscribeAllAsync(CurrencyType currency, CandleResolution resolution, CancellationToken ct = default)
        => throw new NotSupportedException("Server-side candle subscriptions are owned by CandleService; client subscribes per chart.");

    public Task SubscribeAllDefaultAsync(CurrencyType currency, CancellationToken ct = default)
        => throw new NotSupportedException("Server-side candle subscriptions are owned by CandleService; client subscribes per chart.");

    public Task PrimeRingsAsync(IReadOnlyCollection<CurrencyType> currencies,
        IReadOnlyCollection<CandleResolution> resolutions, CancellationToken ct = default)
        => throw new NotSupportedException("Ring priming is a server-side boot step.");

    public Task BackfillUpwardAsync(IReadOnlyCollection<CurrencyType> currencies, CancellationToken ct = default)
        => throw new NotSupportedException("Upward backfill is a server-side boot step.");

    public Task<int> FillCandleGapsAsync(IReadOnlyCollection<CurrencyType> currencies,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => throw new NotSupportedException("Candle gap-fill is a server-side maintenance step.");

    public Task OnTransactionTickAsync(Transaction tick, CancellationToken ct = default) => Task.CompletedTask;
    public void OnTransactionTick(Transaction tick) { /* no-op on client */ }

    public async IAsyncEnumerable<Candle> StreamClosedCandles(int stockId, CurrencyType currency,
        CandleResolution resolution, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = (stockId, currency, resolution);
        Subscribe(stockId, currency, resolution);
        var stream = _streams.GetOrAdd(key, _ => Channel.CreateBounded<Candle>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest }));
        try
        {
            while (await stream.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) yield break;
                while (stream.Reader.TryRead(out var candle))
                {
                    if (ct.IsCancellationRequested) yield break;
                    yield return candle;
                }
            }
        }
        finally
        {
            await UnsubscribeAsync(stockId, currency, resolution).ConfigureAwait(false);
        }
    }

    public Candle? TryGetLiveSnapshot(int stockId, CurrencyType currency, CandleResolution resolution)
    {
        // The live in-progress candle lives on the server aggregator. The
        // client only sees closed candles via the hub. Returning null is the
        // documented null-when-no-data behaviour the chart already tolerates.
        return null;
    }

    public async Task<IReadOnlyList<Candle>> GetHistoricalCandlesAsync(
        int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default, bool fillGaps = false)
    {
        // Backed by the existing GET /api/candles/by-stock-range endpoint.
        // fillGaps and the "replay from transactions if empty" logic live in the
        // server's CandleService.GetHistoricalCandlesAsync — this endpoint hits
        // the DB directly. If gap-fill becomes a chart requirement, add a
        // /historical endpoint that proxies through ICandleService server-side.
        _ = fillGaps;
        var span = TimeSpan.FromSeconds((int)resolution);
        var key = (stockId, currency, resolution);

        // §client cache (steps 4-5): align the request to bucket boundaries exactly as the server does, then
        // try to serve the whole [from,to) from RAM — a fully-covered range at/behind the sealed frontier is
        // an instant switch-back with zero HTTP. A miss (or disabled) falls through to the fetch below.
        var fromAligned = TimeHelper.FloorToBucketUtc(fromUtc, span);
        var toAligned = TimeHelper.NextBucketBoundaryUtc(toUtc, span);
        if (CandleCache.Enabled)
        {
            var cached = _cache.TryServe(stockId, currency, resolution, fromAligned, toAligned);
            if (cached is { Count: > 0 }) return cached;
        }

        var http = _httpFactory.CreateClient("KSE.Server");
        var url = $"api/candles/by-stock-range/{stockId}/{currency}" +
                  $"?resolution={Uri.EscapeDataString(span.ToString())}" +
                  $"&from={Uri.EscapeDataString(fromUtc.ToString("o"))}" +
                  $"&to={Uri.EscapeDataString(toUtc.ToString("o"))}";

        try
        {
            var list = await http.GetFromJsonAsync<List<Candle>>(url, ApiJsonOptions.Default, ct).ConfigureAwait(false);
            // Remember the last non-empty result so a later fault can serve it. An
            // empty result is passed through as-is (young stock / no data in range)
            // and never overwrites a good snapshot.
            if (list is { Count: > 0 })
            {
                _lastGood[key] = list;
                // Warm the cache with the sealed portion of this fetch (buckets before the still-forming one).
                if (CandleCache.Enabled)
                {
                    var forming = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), span);
                    _cache.MergeFetched(stockId, currency, resolution, fromAligned, toAligned, list, forming);
                }
            }
            return list ?? new List<Candle>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Serve-stale-on-fault: a transient fetch failure must not blank a chart
            // that was already showing data. Fall back to the last good snapshot for
            // this key; if we have none yet, there is nothing to show but empty.
            if (_lastGood.TryGetValue(key, out var stale))
            {
                _logger.LogWarning(ex, "Candle history fetch failed for {Key}; serving {Count} stale candles.",
                    key, stale.Count);
                return stale;
            }
            _logger.LogWarning(ex, "Candle history fetch failed for {Key}; no stale cache to serve.", key);
            return new List<Candle>();
        }
    }

    public Task<CandleFixReport> FixCandlesAsync(int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => throw new NotSupportedException("Candle fix-up runs server-side. Use an admin endpoint.");

    public Candle NewCandle(int stockId, CurrencyType currency, DateTime timestamp, TimeSpan resolution, decimal? flatPrice = null)
        => throw new NotSupportedException("Candle construction stays server-side.");

    public Candle AggregateCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution, bool requireFullCoverage = true)
        => throw new NotSupportedException("Candle aggregation stays server-side.");

    public List<Candle> AggregateMultipleCandles(IReadOnlyList<Candle> candles, CandleResolution targetResolution,
        bool requireFullCoverage = true, bool allowPartialEdges = true)
        => throw new NotSupportedException("Candle aggregation stays server-side.");

    public Task<IReadOnlyList<Candle>> AggregateAndPersistRangeAsync(int stockId, CurrencyType currency,
        CandleResolution sourceRes, CandleResolution targetRes, DateTime fromUtc, DateTime toUtc,
        bool allowPartialEdges = true, CancellationToken ct = default)
        => throw new NotSupportedException("Candle aggregation stays server-side.");

    public ValueTask DisposeAsync()
    {
        _hub.CandleClosed -= OnHubCandleClosed;
        foreach (var s in _streams.Values) s.Writer.TryComplete();
        _streams.Clear();
        return ValueTask.CompletedTask;
    }
}
