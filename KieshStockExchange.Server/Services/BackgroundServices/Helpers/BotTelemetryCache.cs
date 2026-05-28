using KieshStockExchange.Helpers;
using KieshStockExchange.Server.Controllers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// TTL cache + single-flight gate for the BotDashboard's two expensive
/// admin reads (<c>/last-24h-stats</c> and <c>/activity-buckets</c>). Each
/// read scans every transaction since the 24h cutoff on a multi-GB DB;
/// without a cache the dashboard's 10s poll would re-run the scan from
/// scratch every tick. With it, the first call after TTL pays the scan
/// cost and the rest read the cached value in microseconds.
/// </summary>
public sealed class BotTelemetryCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly IDataBaseService _db;
    private readonly IAiTradeService _bots;
    private readonly ILogger<BotTelemetryCache> _logger;

    private (DateTime ExpiresUtc, BotLast24hStats Value)? _last24h;
    private readonly SemaphoreSlim _last24hGate = new(1, 1);

    private readonly ConcurrentDictionary<BucketKey, BucketCacheEntry> _buckets = new();

    public BotTelemetryCache(IDataBaseService db, IAiTradeService bots, ILogger<BotTelemetryCache> logger)
    {
        _db = db;
        _bots = bots;
        _logger = logger;
    }

    public async Task<BotLast24hStats> GetLast24hAsync(CancellationToken ct)
    {
        var now = TimeHelper.NowUtc();
        if (_last24h is { ExpiresUtc: var exp, Value: var cached } && exp > now) return cached;

        await _last24hGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-checked under the gate so concurrent callers share a scan.
            if (_last24h is { ExpiresUtc: var e2, Value: var v2 } && e2 > now) return v2;

            var fresh = await ComputeLast24hAsync(ct).ConfigureAwait(false);
            _last24h = (now + Ttl, fresh);
            return fresh;
        }
        finally { _last24hGate.Release(); }
    }

    public async Task<BotActivityBuckets> GetActivityBucketsAsync(
        DateTime fromUtc, DateTime toUtc, int bucketCount, CancellationToken ct)
    {
        // Key on (range duration, bucket count) only — the dashboard re-anchors
        // its window to "now" every poll, but the bucket grid shape only
        // depends on the duration. Slight staleness of up to TTL seconds is
        // acceptable for the 15m–24h chart ranges.
        var rangeSeconds = (long)Math.Max(0, (toUtc - fromUtc).TotalSeconds);
        var key = new BucketKey(rangeSeconds, bucketCount);
        var entry = _buckets.GetOrAdd(key, _ => new BucketCacheEntry());

        var now = TimeHelper.NowUtc();
        if (entry.ExpiresUtc > now && entry.Value is { } cached) return cached;

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.ExpiresUtc > now && entry.Value is { } cachedTwo) return cachedTwo;

            var fresh = await ComputeBucketsAsync(fromUtc, toUtc, bucketCount, ct).ConfigureAwait(false);
            entry.Value = fresh;
            entry.ExpiresUtc = now + Ttl;
            return fresh;
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>Pre-populate the most common dashboard queries so the first client
    /// poll after server start hits a warm cache instead of the cold DB scan.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        try
        {
            await GetLast24hAsync(ct).ConfigureAwait(false);

            // Match the BotDashboardViewModel's defaults: 60 buckets across 1h.
            var now = TimeHelper.NowUtc();
            await GetActivityBucketsAsync(now - TimeSpan.FromHours(1), now, 60, ct).ConfigureAwait(false);

            _logger.LogInformation("BotTelemetryCache warm-up complete.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotTelemetryCache warm-up failed; first dashboard poll will pay the cold-scan cost.");
        }
    }

    private async Task<BotLast24hStats> ComputeLast24hAsync(CancellationToken ct)
    {
        var since = TimeHelper.NowUtc() - TimeSpan.FromHours(24);
        var aiIds = new HashSet<int>(_bots.GetAiUserIds());
        if (aiIds.Count == 0) return new BotLast24hStats(0, 0m, 0);

        var txs = await _db.GetTransactionsSinceTime(since, limit: null, ct).ConfigureAwait(false);
        int trades = 0;
        decimal volume = 0m;
        var participants = new HashSet<int>();
        for (int i = 0; i < txs.Count; i++)
        {
            var t = txs[i];
            bool buyerAi = aiIds.Contains(t.BuyerId);
            bool sellerAi = aiIds.Contains(t.SellerId);
            if (!buyerAi && !sellerAi) continue;
            trades++;
            volume += t.TotalAmount;
            if (buyerAi) participants.Add(t.BuyerId);
            if (sellerAi) participants.Add(t.SellerId);
        }
        return new BotLast24hStats(trades, volume, participants.Count);
    }

    private async Task<BotActivityBuckets> ComputeBucketsAsync(
        DateTime fromUtc, DateTime toUtc, int bucketCount, CancellationToken ct)
    {
        var aiIds = new HashSet<int>(_bots.GetAiUserIds());
        var trades = new int[bucketCount];
        var volume = new decimal[bucketCount];

        if (aiIds.Count == 0) return new BotActivityBuckets(trades, volume);

        var rangeTicks = (toUtc - fromUtc).Ticks;
        var bucketTicks = rangeTicks / bucketCount;
        if (bucketTicks <= 0) return new BotActivityBuckets(trades, volume);

        var txs = await _db.GetTransactionsSinceTime(fromUtc, limit: null, ct).ConfigureAwait(false);
        for (int i = 0; i < txs.Count; i++)
        {
            var tx = txs[i];
            if (tx.Timestamp >= toUtc) continue;
            bool buyerAi = aiIds.Contains(tx.BuyerId);
            bool sellerAi = aiIds.Contains(tx.SellerId);
            if (!buyerAi && !sellerAi) continue;
            var offset = (tx.Timestamp - fromUtc).Ticks;
            if (offset < 0) continue;
            int idx = (int)(offset / bucketTicks);
            if (idx >= bucketCount) idx = bucketCount - 1;
            trades[idx]++;
            volume[idx] += tx.TotalAmount;
        }
        return new BotActivityBuckets(trades, volume);
    }

    private readonly record struct BucketKey(long RangeSeconds, int BucketCount);

    private sealed class BucketCacheEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTime ExpiresUtc { get; set; } = DateTime.MinValue;
        public BotActivityBuckets? Value { get; set; }
    }
}
