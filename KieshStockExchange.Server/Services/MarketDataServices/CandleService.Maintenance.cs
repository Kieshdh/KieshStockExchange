using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices;
using KieshStockExchange.Services.DataServices.Interfaces;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class CandleService
{
    public async Task BackfillUpwardAsync(IReadOnlyCollection<CurrencyType> currencies, CancellationToken ct = default)
    {
        if (currencies is null || currencies.Count == 0) return;
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var stocks = _stock.All;
        var nowAligned = TimeHelper.NowUtc();
        const int WindowDays = 60;
        var from = nowAligned - TimeSpan.FromDays(WindowDays);

        // Every chart-visible higher resolution is an integer multiple of 5m
        // (15m=3, 1h=12, 4h=48, 1d=288), so we can aggregate them all from a
        // single source. The bot loop keeps 5m subscribed for every stock by
        // default, so this source is the densest and most reliable.
        var source = CandleResolution.FiveMinutes;
        var targets = new[]
        {
            CandleResolution.FifteenMinutes,
            CandleResolution.OneHour,
            CandleResolution.FourHours,
            CandleResolution.OneDay,
        };

        int producedCandles = 0;
        int failedCombos = 0;

        foreach (var s in stocks)
        {
            if (ct.IsCancellationRequested) return;
            foreach (var ccy in currencies)
            {
                if (!_stock.IsListedIn(s.StockId, ccy)) continue;

                List<Candle>? srcSnapshot = null;
                foreach (var target in targets)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        // Fetch the 5m source once per (stock, currency) and reuse
                        // it for all target resolutions — avoids repeated DB scans.
                        if (srcSnapshot is null)
                        {
                            var src = await GetHistoricalCandlesAsync(
                                s.StockId, ccy, source, from, nowAligned, ct, fillGaps: false).ConfigureAwait(false);
                            if (src.Count == 0) break; // no 5m data → nothing to aggregate
                            srcSnapshot = src as List<Candle> ?? new List<Candle>(src);
                        }

                        // requireFullCoverage:false so a missing 5m here-or-there
                        // (e.g. brief outage between sessions) doesn't blow up the
                        // entire pass with "must provide continuous coverage".
                        var aggregated = AggregateMultipleCandles(
                            srcSnapshot, target, requireFullCoverage: false, allowPartialEdges: true);
                        if (aggregated.Count == 0) continue;

                        await _db.UpsertCandlesAsync(aggregated, ct).ConfigureAwait(false);
                        producedCandles += aggregated.Count;
                    }
                    catch (Exception ex)
                    {
                        failedCombos++;
                        _logger.LogDebug(ex,
                            "Upward backfill failed for stock={Stock} ccy={Ccy} 5m->{Target}",
                            s.StockId, ccy, target);
                    }
                }
            }
        }
        _logger.LogInformation(
            "Upward candle backfill: persisted {Candles} candles across 15m/1h/4h/1d ({Window}d window). " +
            "Failed combos: {Failed}.",
            producedCandles, WindowDays, failedCombos);
    }

    // Bottom-up ladder. Each rung is built from the one directly below it; because
    // we cascade lowest→highest, a 5m gap filled from 1m in this pass is itself a
    // source for the 15m fill that follows. Adjacent steps are all integer multiples
    // (60/15, 300/60, 900/300, 3600/900, 14400/3600, 86400/14400).
    private static readonly CandleResolution[] GapLadder =
    {
        CandleResolution.FifteenSeconds, CandleResolution.OneMinute, CandleResolution.FiveMinutes,
        CandleResolution.FifteenMinutes, CandleResolution.OneHour, CandleResolution.FourHours,
        CandleResolution.OneDay,
    };

    public async Task<int> FillCandleGapsAsync(IReadOnlyCollection<CurrencyType> currencies,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (currencies is null || currencies.Count == 0 || toUtc <= fromUtc) return 0;
        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);

        int filled = 0;
        int failedCombos = 0;
        foreach (var s in _stock.All)
        {
            if (ct.IsCancellationRequested) break;
            foreach (var ccy in currencies)
            {
                if (!_stock.IsListedIn(s.StockId, ccy)) continue;
                for (int i = 1; i < GapLadder.Length; i++)
                {
                    if (ct.IsCancellationRequested) return filled;
                    try
                    {
                        filled += await FillOneResolutionGapAsync(
                            s.StockId, ccy, source: GapLadder[i - 1], target: GapLadder[i],
                            fromUtc, toUtc, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failedCombos++;
                        _logger.LogDebug(ex, "Gap-fill failed for stock={Stock} ccy={Ccy} {Src}->{Tgt}",
                            s.StockId, ccy, GapLadder[i - 1], GapLadder[i]);
                    }
                }
            }
        }
        _logger.LogInformation(
            "Candle gap-fill: synthesized {Filled} missing candle(s) over {From:u}..{To:u}. Failed combos: {Failed}.",
            filled, fromUtc, toUtc, failedCombos);
        return filled;
    }

    /// <summary>
    /// Fills the <paramref name="target"/>-resolution buckets in [from, to) that have
    /// a finer source candle but no persisted target candle. Reads the source straight
    /// from the DB (so it sees finer rungs already filled earlier in the same cascade),
    /// aggregates lenient (requireFullCoverage:false), and upserts only the absent
    /// buckets — existing candles are never overwritten.
    /// </summary>
    private async Task<int> FillOneResolutionGapAsync(
        int stockId, CurrencyType currency, CandleResolution source, CandleResolution target,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var targetSpan = TimeSpan.FromSeconds((int)target);
        var from = TimeHelper.FloorToBucketUtc(fromUtc, targetSpan);
        var to = TimeHelper.NextBucketBoundaryUtc(toUtc, targetSpan);

        var src = await _db.GetCandlesByStockIdAndTimeRange(
            stockId, currency, TimeSpan.FromSeconds((int)source), from, to, ct).ConfigureAwait(false);
        if (src.Count == 0) return 0;
        src.Sort(static (a, b) => a.OpenTime.CompareTo(b.OpenTime));

        var existing = await _db.GetCandlesByStockIdAndTimeRange(
            stockId, currency, targetSpan, from, to, ct).ConfigureAwait(false);
        var have = new HashSet<DateTime>(existing.Select(c => c.OpenTime));

        var aggregated = AggregateMultipleCandles(src, target, requireFullCoverage: false, allowPartialEdges: true);
        var missing = aggregated.Where(c => !have.Contains(c.OpenTime)).ToList();
        if (missing.Count == 0) return 0;

        await _db.UpsertCandlesAsync(missing, ct).ConfigureAwait(false);
        return missing.Count;
    }

    public async Task PrimeRingsAsync(IReadOnlyCollection<CurrencyType> currencies,
        IReadOnlyCollection<CandleResolution> resolutions, CancellationToken ct = default)
    {
        if (currencies is null || currencies.Count == 0) return;
        if (resolutions is null || resolutions.Count == 0) return;

        await _stock.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var stocks = _stock.All;
        var nowAligned = TimeHelper.NowUtc();
        int primedFromDb = 0;
        int primedFromReplay = 0;
        int emptyKeys = 0;

        foreach (var s in stocks)
        {
            foreach (var ccy in currencies)
            {
                if (!_stock.IsListedIn(s.StockId, ccy)) continue;

                foreach (var res in resolutions)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var span = TimeSpan.FromSeconds((int)res);
                        // Look back exactly RingCapacity buckets so the DB scan
                        // is narrow and any transaction replay only walks the
                        // window we care about.
                        var to = TimeHelper.NextBucketBoundaryUtc(nowAligned, span);
                        var from = to - span * RingCapacity;

                        // Snapshot what's in the DB first so we can tell whether
                        // GetHistoricalCandlesAsync hit the cached rows or had to
                        // replay transactions to manufacture them. Cheap COUNT-shaped
                        // call would be ideal, but the existing helper returns the
                        // list; we just check whether replay happened by comparing
                        // before/after.
                        var preCount = (await _db.GetCandlesByStockIdAndTimeRange(
                            s.StockId, ccy, span, from, to, ct).ConfigureAwait(false)).Count;

                        // Route through the smart historical method so the
                        // DB → transaction-replay → persist waterfall fires for
                        // keys that have raw trades but no persisted candles yet.
                        var candles = await GetHistoricalCandlesAsync(
                            s.StockId, ccy, res, from, to, ct, fillGaps: false).ConfigureAwait(false);

                        if (candles.Count == 0) { emptyKeys++; continue; }

                        // Candles arrive ascending by OpenTime; push them in that
                        // order so the ring's FIFO iteration matches wall clock.
                        var ring = GetOrAddRing((s.StockId, ccy, res));
                        for (int i = 0; i < candles.Count; i++)
                            ring.Push(candles[i]);

                        if (preCount == 0) primedFromReplay++; else primedFromDb++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ring prime failed for {Stock}/{Ccy}/{Res}", s.StockId, ccy, res);
                    }
                }
            }
        }
        _logger.LogInformation(
            "Candle rings primed: {FromDb} from DB, {FromReplay} via transaction replay, {Empty} keys had no data " +
            "(stocks={Stocks}, currencies={Currencies}, resolutions={Resolutions})",
            primedFromDb, primedFromReplay, emptyKeys,
            stocks.Count, currencies.Count, resolutions.Count);
    }

    public async Task<CandleFixReport> FixCandlesAsync(
        int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var key = (stockId, currency, resolution);
        CheckKey(key);

        // Determine bucket-aligned time range
        var (bucket, from, to) = CandleAggregationMath.AlignRange(resolution, fromUtc, toUtc);

        // Load existing candles in range
        var existing = await _db.GetCandlesByStockIdAndTimeRange(
            stockId, currency, bucket, from, to, ct).ConfigureAwait(false);
        var existingByKey = new HashSet<Candle>(existing, CandleKeyComparer.Instance);

        // Load all transactions in range
        var ticks = await _db.GetTransactionsByStockIdAndTimeRange(
            stockId, currency, from, to, ct: ct).ConfigureAwait(false);
        if (ticks.Count == 0) return EmptyCandlesReport(stockId, currency, resolution, fromUtc, toUtc);

        // Build all candles using a temporary aggregator
        var closed = ReplayTicksBuildClosed(stockId, currency, resolution, ticks, toUtc, ct);

        // Find missing candles and persist
        var(missedCount, missedTxCount, firstMissing, lastMissing) = 
            await FindMissingAndPersist(key, existingByKey, closed, ct).ConfigureAwait(false);

        // Fix wrong candles and persist
        var (fixedCount, missedTxCount2) = await FindWrongAndPersist(
            key, existingByKey, closed, ct).ConfigureAwait(false);

        // Return report
        return new CandleFixReport(stockId, currency, resolution, fromUtc, toUtc,
            MissingCandleCount: missedCount, FixedCandleCount: fixedCount,
            MissedTxCount: missedTxCount + missedTxCount2, TotalTxCount: ticks.Count, 
            FirstMissing: firstMissing, LastMissing: lastMissing
        );
    }

    private List<Candle> ReplayTicksBuildClosed(
        int stockId, CurrencyType currency, CandleResolution resolution,
        List<Transaction> ticks, DateTime ToUtc, CancellationToken ct)
    {
        // Build using a temporary aggregator
        var agg = new CandleAggregator(stockId, currency, resolution, _logger, true);
        foreach (var t in ticks.OrderBy(t => t.Timestamp))
        {
            if (ct.IsCancellationRequested) break;
            agg.OnTick(t);
        }
        agg.FlushIfElapsed(ToUtc); // Flush everything that fully elapsed before 'toUtc'
        return agg.DrainClosedCandles();
    }

    private async Task<(int MissedCount, int MissedTxCount, DateTime? First, DateTime? Last)> FindMissingAndPersist(
        (int, CurrencyType, CandleResolution) key, HashSet<Candle> existingCandles, List<Candle> allCandles, CancellationToken ct)
    {
        // Find missing candles and persist
        var missing = allCandles.Where(c => !existingCandles.Contains(c)).ToList();

        // Persist missing
        await PersistAndPublishAsync(key, missing, ct).ConfigureAwait(false);

        // Build report data
        DateTime? firstMissing = missing.Count > 0 ? missing.Min(c => c.OpenTime) : null;
        DateTime? lastMissing = missing.Count > 0 ? missing.Max(c => c.OpenTime) : null;
        int missedTxCount = missing.Sum(c => c.TradeCount);
        int missedCount = missing.Count;
        return (missedCount, missedTxCount, firstMissing, lastMissing);
    }

    private async Task<(int FixedCount, int MissedTxCount)> FindWrongAndPersist((int, CurrencyType, CandleResolution) key, 
        HashSet<Candle> existingCandles, List<Candle> allCandles, CancellationToken ct)
    {
        var wrong = new List<Candle>();
        int missedTxCount = 0;

        // Find wrong candles (same key but different OHLCV)
        foreach (var c in allCandles)
        {
            if (existingCandles.TryGetValue(c, out var match) && !CandleFullComparer.Instance.Equals(match, c))
            {
                wrong.Add(c);
                missedTxCount += Math.Max(0, c.TradeCount - match.TradeCount);
            }
        }

        // Persist corrected
        await PersistAndPublishAsync(key, wrong, ct).ConfigureAwait(false);
        return (wrong.Count, missedTxCount);
    }

    private static CandleFixReport EmptyCandlesReport(int stockId, CurrencyType currency, CandleResolution resolution,
        DateTime fromUtc, DateTime toUtc) =>
        new CandleFixReport(stockId, currency, resolution, fromUtc, toUtc,
            MissingCandleCount: 0, FixedCandleCount: 0,
            MissedTxCount: 0, TotalTxCount: 0,
            FirstMissing: null, LastMissing: null
        );

    public async Task<IReadOnlyList<Candle>> AggregateAndPersistRangeAsync(
        int stockId, CurrencyType currency, CandleResolution sourceRes, CandleResolution targetRes,
        DateTime fromUtc, DateTime toUtc, bool allowPartialEdges = true, CancellationToken ct = default)
    {
        // Validate target resolution is a strict multiple of source resolution
        if ((int)targetRes <= (int)sourceRes || (int)targetRes % (int)sourceRes != 0)
            throw new ArgumentOutOfRangeException(nameof(targetRes),
                "Target resolution must be a strict multiple of source resolution.");

        // Load source candles
        var src = await GetHistoricalCandlesAsync(
            stockId, currency, sourceRes, fromUtc, toUtc, ct).ConfigureAwait(false);

        // Aggregate to target resolution
        var aggregated = AggregateMultipleCandles(src, targetRes, true, allowPartialEdges);

        // Persist to DB (UpsertCandlesAsync is atomic server-side)
        await _db.UpsertCandlesAsync(aggregated, ct).ConfigureAwait(false);

        return aggregated;
    }
}
