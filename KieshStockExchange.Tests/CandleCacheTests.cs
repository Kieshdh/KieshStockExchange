using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// Client candle history cache (candle-cache plan steps 4-5): cache-first serve, forming-bar exclusion,
/// live-close frontier advance, LoadOlder extend-down, disjoint replace, and reconnect tail-invalidation.
/// Pure logic over Shared types — no SignalR/HTTP.
/// </summary>
public class CandleCacheTests
{
    private const int Sid = 7;
    private const CurrencyType Ccy = CurrencyType.USD;
    private static readonly CandleResolution Res = (CandleResolution)60; // 1-min buckets
    private const long Base = 1_700_000_040;                             // 60-aligned epoch second

    private static DateTime Open(int i) => DateTimeOffset.FromUnixTimeSeconds(Base + i * 60L).UtcDateTime;

    private static Candle C(int i, decimal price = 100m) => new()
    {
        StockId = Sid, CurrencyType = Ccy, BucketSeconds = 60, OpenTime = Open(i),
        Open = price, High = price, Low = price, Close = price, Volume = 1, TradeCount = 1,
    };

    private static List<Candle> Range(int lo, int hiExclusive)
    {
        var l = new List<Candle>();
        for (int i = lo; i < hiExclusive; i++) l.Add(C(i));
        return l;
    }

    [Fact]
    public void Empty_cache_misses()
        => Assert.Null(new CandleCache().TryServe(Sid, Ccy, Res, Open(0), Open(10)));

    [Fact]
    public void Fetch_then_full_hit_returns_slice()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), formingBucketOpenUtc: Open(10));
        var hit = cache.TryServe(Sid, Ccy, Res, Open(0), Open(10));
        Assert.NotNull(hit);
        Assert.Equal(10, hit!.Count);
        Assert.Equal(Open(0), hit[0].OpenTime);
        Assert.Equal(Open(9), hit[^1].OpenTime);
    }

    [Fact]
    public void Sub_range_hit_slices_correctly()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), Open(10));
        var hit = cache.TryServe(Sid, Ccy, Res, Open(3), Open(7));
        Assert.NotNull(hit);
        Assert.Equal(new[] { Open(3), Open(4), Open(5), Open(6) }, hit!.Select(c => c.OpenTime));
    }

    [Fact]
    public void Request_past_sealed_frontier_misses()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), Open(10));
        Assert.Null(cache.TryServe(Sid, Ccy, Res, Open(0), Open(11)));  // to beyond CoveredTo
    }

    [Fact]
    public void Request_below_covered_from_misses()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(5), Open(10), Range(5, 10), Open(10));
        Assert.Null(cache.TryServe(Sid, Ccy, Res, Open(0), Open(10)));  // from below coverage
    }

    [Fact]
    public void Forming_bucket_is_excluded_from_cache()
    {
        var cache = new CandleCache();
        // Fetch 0..10 but bucket 10 is the still-forming bucket ⇒ not sealed, not cached.
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(11), Range(0, 11), formingBucketOpenUtc: Open(10));
        Assert.Null(cache.TryServe(Sid, Ccy, Res, Open(0), Open(11)));   // bucket 10 not final ⇒ miss
        var hit = cache.TryServe(Sid, Ccy, Res, Open(0), Open(10));      // up to the frontier ⇒ hit
        Assert.NotNull(hit);
        Assert.Equal(10, hit!.Count);
        Assert.DoesNotContain(hit, c => c.OpenTime == Open(10));
    }

    [Fact]
    public void Live_close_of_next_bucket_advances_frontier()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), Open(10));
        cache.MergeClosed(Sid, Ccy, Res, C(10));                        // exact next bucket
        var hit = cache.TryServe(Sid, Ccy, Res, Open(0), Open(11));
        Assert.NotNull(hit);
        Assert.Equal(11, hit!.Count);
        Assert.Equal(Open(10), hit[^1].OpenTime);
    }

    [Fact]
    public void Live_close_forward_gap_is_ignored()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), Open(10));
        cache.MergeClosed(Sid, Ccy, Res, C(12));                        // gap (bucket 10,11 missing)
        Assert.Null(cache.TryServe(Sid, Ccy, Res, Open(0), Open(11)));  // frontier not advanced ⇒ still misses
        Assert.NotNull(cache.TryServe(Sid, Ccy, Res, Open(0), Open(10)));
    }

    [Fact]
    public void Live_close_within_span_is_idempotent_replace()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), Open(10));
        cache.MergeClosed(Sid, Ccy, Res, C(5, price: 200m));            // refresh an already-sealed bucket
        var hit = cache.TryServe(Sid, Ccy, Res, Open(0), Open(10));
        Assert.NotNull(hit);
        Assert.Equal(10, hit!.Count);                                  // no duplicate inserted
        Assert.Equal(200m, hit!.Single(c => c.OpenTime == Open(5)).Close);
    }

    [Fact]
    public void Load_older_extends_coverage_down()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(5), Open(10), Range(5, 10), Open(10));
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(5), Range(0, 5), Open(10)); // adjacent below
        var hit = cache.TryServe(Sid, Ccy, Res, Open(0), Open(10));
        Assert.NotNull(hit);
        Assert.Equal(10, hit!.Count);
    }

    [Fact]
    public void Disjoint_fetch_replaces_entry()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(5), Range(0, 5), Open(20));
        cache.MergeFetched(Sid, Ccy, Res, Open(10), Open(15), Range(10, 15), Open(20)); // gap between spans
        Assert.Null(cache.TryServe(Sid, Ccy, Res, Open(0), Open(5)));   // old span dropped
        Assert.NotNull(cache.TryServe(Sid, Ccy, Res, Open(10), Open(15)));
    }

    [Fact]
    public void InvalidateTail_drops_tail_and_pulls_frontier()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), Open(10));
        cache.InvalidateTail(Sid, Ccy, Res, Open(7));
        Assert.Null(cache.TryServe(Sid, Ccy, Res, Open(0), Open(10)));  // buckets 7-9 gone ⇒ miss
        var hit = cache.TryServe(Sid, Ccy, Res, Open(0), Open(7));
        Assert.NotNull(hit);
        Assert.Equal(7, hit!.Count);
        Assert.DoesNotContain(hit, c => c.OpenTime >= Open(7));
    }

    [Fact]
    public void Keys_are_isolated_by_resolution()
    {
        var cache = new CandleCache();
        cache.MergeFetched(Sid, Ccy, Res, Open(0), Open(10), Range(0, 10), Open(10));
        Assert.Null(cache.TryServe(Sid, Ccy, (CandleResolution)300, Open(0), Open(10))); // different res
    }
}
