using System.Collections.Concurrent;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// Client-side history cache for CLOSED candles, keyed by (stock, currency, resolution). Backs
/// SignalRCandleService so a timeframe switch-back serves from RAM (zero HTTP) and live closes keep every
/// backgrounded resolution warm (the "subscription" of the candle-cache plan, steps 4-5). Lives in Shared
/// (pure logic over Shared types) so it is unit-testable; the client service owns the instance.
///
/// Invariants that keep it safe on the live chart path:
///  • Stores ONLY closed, immutable candles over ONE contiguous span [CoveredFrom, CoveredTo).
///  • CoveredToUnix is the SEALED FRONTIER (exclusive): every cached OpenTime &lt; it is final. The forming
///    bar is never cached — the VM's live-sync owns the right edge — so a read whose upper bound reaches
///    past the frontier MISSES and falls through to a fresh fetch.
///  • A closed candle from the hub extends the frontier only when it is the exact next contiguous bucket;
///    a gap leaves the tail short so the next read re-fetches it (safe degradation, never a silent hole).
/// This is a transport/latency layer only: it stores whatever the server produced and never authors O/H/L/C.
/// </summary>
public sealed class CandleCache
{
    // Client cache-first read path (candle-cache plan steps 4-5). Set once at client startup from
    // Candles:ClientCache (MauiProgram). Default false ⇒ SignalRCandleService always fetches over HTTP
    // exactly as before (byte-identical) — the cache is populated but never served, so flipping it on is a
    // reversible, low-risk switch once verified on a running client.
    public static bool Enabled = false;

    private sealed class Entry
    {
        public readonly List<Candle> Candles = new();   // ascending by OpenTime, distinct, closed-only
        public long CoveredFromUnix;                     // inclusive
        public long CoveredToUnix;                       // exclusive — sealed frontier (all cached OpenTime < this)
    }

    private readonly ConcurrentDictionary<(int, CurrencyType, CandleResolution), Entry> _byKey = new();
    private readonly object _gate = new();               // entries are tiny; one lock keeps merges/reads consistent

    public static long ToUnix(DateTime utc)
        => ((DateTimeOffset)DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>
    /// Serve [fromUtc, toUtc) entirely from cache, or null on a miss. A hit requires the covered span to
    /// contain the request AND the request's upper bound to sit at/behind the sealed frontier (so the tail
    /// is final, never a forming bar).
    /// </summary>
    public List<Candle>? TryServe(int stockId, CurrencyType currency, CandleResolution res,
        DateTime fromUtc, DateTime toUtc)
    {
        var from = ToUnix(fromUtc);
        var to = ToUnix(toUtc);
        lock (_gate)
        {
            if (!_byKey.TryGetValue((stockId, currency, res), out var e)) return null;
            if (e.Candles.Count == 0) return null;
            if (from < e.CoveredFromUnix || to > e.CoveredToUnix) return null; // not fully covered / past frontier
            var slice = new List<Candle>();
            foreach (var c in e.Candles)
            {
                var t = ToUnix(c.OpenTime);
                if (t >= from && t < to) slice.Add(c);
            }
            return slice;
        }
    }

    /// <summary>
    /// Merge a fresh fetch. <paramref name="formingBucketOpenUtc"/> = floor(now) to the resolution — the open
    /// of the still-forming bucket; only candles strictly before it are sealed and cached. The covered span
    /// extends when the fetch overlaps/abuts existing coverage; a disjoint fetch replaces the entry.
    /// </summary>
    public void MergeFetched(int stockId, CurrencyType currency, CandleResolution res,
        DateTime fromUtc, DateTime toAlignedUtc, IReadOnlyList<Candle> fetched, DateTime formingBucketOpenUtc)
    {
        var sealedTo = Math.Min(ToUnix(toAlignedUtc), ToUnix(formingBucketOpenUtc));
        var from = ToUnix(fromUtc);
        if (sealedTo <= from) return; // nothing sealed in range (e.g. requesting only the forming bucket)

        lock (_gate)
        {
            var key = (stockId, currency, res);
            var e = _byKey.TryGetValue(key, out var existing) ? existing : null;

            // Disjoint from existing coverage ⇒ start fresh (chart reads are normally overlapping/adjacent).
            if (e != null && (from > e.CoveredToUnix || sealedTo < e.CoveredFromUnix))
                e = null;

            e ??= new Entry { CoveredFromUnix = from, CoveredToUnix = from };
            foreach (var c in fetched)
            {
                if (ToUnix(c.OpenTime) < sealedTo) UpsertSorted(e.Candles, c);
            }
            e.CoveredFromUnix = Math.Min(e.CoveredFromUnix, from);
            e.CoveredToUnix = Math.Max(e.CoveredToUnix, sealedTo);
            _byKey[key] = e;
        }
    }

    /// <summary>
    /// Fold a closed candle from the hub into the matching entry. Extends the sealed frontier only when the
    /// candle is the exact next contiguous bucket; an already-covered bucket is replaced in place (idempotent);
    /// a forward gap is ignored so the next read re-fetches the tail. No-op if the key isn't cached yet.
    /// </summary>
    public void MergeClosed(int stockId, CurrencyType currency, CandleResolution res, Candle closed)
    {
        var span = (int)res;
        var open = ToUnix(closed.OpenTime);
        lock (_gate)
        {
            if (!_byKey.TryGetValue((stockId, currency, res), out var e)) return;
            if (open < e.CoveredToUnix)          // already within the sealed span — idempotent refresh
            {
                UpsertSorted(e.Candles, closed);
            }
            else if (open == e.CoveredToUnix)    // exact next bucket — append and advance the frontier
            {
                UpsertSorted(e.Candles, closed);
                e.CoveredToUnix = open + span;
            }
            // else: forward gap (dropped closes) — leave the tail short; the next read misses and re-fetches.
        }
    }

    /// <summary>Drop cached buckets at/after <paramref name="fromUtc"/> and pull the frontier back — used on
    /// reconnect (step 6) to force a tail re-fetch after possibly-missed closes.</summary>
    public void InvalidateTail(int stockId, CurrencyType currency, CandleResolution res, DateTime fromUtc)
    {
        var from = ToUnix(fromUtc);
        lock (_gate)
        {
            if (!_byKey.TryGetValue((stockId, currency, res), out var e)) return;
            e.Candles.RemoveAll(c => ToUnix(c.OpenTime) >= from);
            if (from < e.CoveredToUnix) e.CoveredToUnix = Math.Max(e.CoveredFromUnix, from);
        }
    }

    // Insert keeping ascending-by-OpenTime order with no duplicate OpenTime (replace on match).
    private static void UpsertSorted(List<Candle> list, Candle c)
    {
        var t = c.OpenTime;
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].OpenTime < t) lo = mid + 1; else hi = mid;
        }
        if (lo < list.Count && list[lo].OpenTime == t) list[lo] = c;   // replace same-bucket
        else list.Insert(lo, c);
    }
}
