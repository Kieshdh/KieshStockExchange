using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

internal enum DayBoundaryMode { ServiceStart, UtcMidnight }

/// <summary>
/// Two-timescale per-(stock,currency) price memory: a short EWMA of the loop's smoothed price
/// (medium-term anchor target) and a time-weighted daily average that rotates at a config-driven
/// boundary (long-term anchor target). Drives the three-tier value anchor in
/// <see cref="AiBotDecisionService"/>: the medium-term EWMA gives a stock that rips faster than
/// its own recent regime a negative-feedback pull back to that regime; the rotated daily average
/// replaces the OU walk as the long-anchor target when <c>UsePreviousDayAverage</c> is on.
///
/// Conservation-neutral: places no orders, holds no balances — only shapes the price bots aim at.
/// Deterministic, RNG-free. Driven by one <see cref="Tick"/> per bot-loop iteration (~1 Hz),
/// loop-thread only, no locks (mirrors <see cref="FundamentalService"/> and
/// <see cref="BotSentimentService"/>). Inert until <see cref="Reset"/>. When constructed with
/// <c>anyConsumer=false</c> the whole Tick body short-circuits at the top — byte-identical to a
/// world without the service.
/// </summary>
internal sealed class BotPriceMemoryService
{
    // Mirror BotSentimentService: clamp dt so a stalled or first-after-reset tick can't poison
    // the EWMA or the day-window accumulator.
    private const double MinDtSec = 0.05;
    private const double MaxDtSec = 60.0;

    private readonly IStockService _stocks;
    private readonly ILogger<BotPriceMemoryService> _logger;
    private readonly Func<(int, CurrencyType), decimal> _priceLookup;

    private readonly bool    _anyConsumer;
    private readonly double  _halfLifeSec;
    private readonly double  _dayLengthSec;
    private readonly DayBoundaryMode _boundary;
    private readonly decimal _maxDailyDrift;
    private readonly int     _windowDays;

    // Per-(stock,currency) state. Plain Dictionary<> — single loop-thread mutator, same pattern
    // as FundamentalService._current / BotSentimentService._combined.
    private readonly Dictionary<(int, CurrencyType), decimal> _seed = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _recent = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _daySumPriceDt = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _daySumDt = new();
    // §weighted-week: rolling history of the last WindowDays daily TWAPs. Queue head = oldest,
    // tail = most recent. Trimmed on rotation. When WindowDays=1 this collapses to the previous
    // single-day-snapshot behaviour (one entry, weight 1, byte-identical to the prior design).
    private readonly Dictionary<(int, CurrencyType), Queue<decimal>> _dayHistory = new();

    private bool _havePrev;
    private DateTime _lastTickUtc = DateTime.MaxValue; // MaxValue = inert until Reset
    private DateTime _windowStartUtc = DateTime.MaxValue;

    internal BotPriceMemoryService(IStockService stocks, ILogger<BotPriceMemoryService> logger,
        Func<(int, CurrencyType), decimal> priceLookup,
        bool anyConsumer = false,
        double halfLifeSec = 1800.0, double dayLengthHours = 24.0,
        DayBoundaryMode boundary = DayBoundaryMode.ServiceStart,
        decimal maxDailyDrift = 0.50m,
        int windowDays = 1)
    {
        _stocks      = stocks      ?? throw new ArgumentNullException(nameof(stocks));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));
        _priceLookup = priceLookup ?? throw new ArgumentNullException(nameof(priceLookup));
        _anyConsumer  = anyConsumer;
        // Defensive sanity: halflife and dayLength must be > MinDtSec or the EWMA / rotation math
        // explodes; maxDrift < 1.0 so the lower band stays > 0; windowDays >= 1 so the weighted
        // average always has at least one slot.
        _halfLifeSec  = Math.Max(MinDtSec, halfLifeSec);
        _dayLengthSec = Math.Max(MinDtSec, dayLengthHours * 3600.0);
        _maxDailyDrift = Math.Clamp(maxDailyDrift, 0m, 0.99m);
        _boundary     = boundary;
        _windowDays   = Math.Max(1, windowDays);
    }

    /// <summary>
    /// Seed every (stock,currency) at its listing seed price, clear the EWMA / day accumulators,
    /// and arm the tick clock. Mirrors <see cref="FundamentalService.Reset"/> /
    /// <see cref="BotSentimentService.Reset"/>. Multiple Reset cycles are safe — every state is
    /// reset, including the day window.
    /// </summary>
    internal void Reset(DateTime now)
    {
        _seed.Clear();
        _recent.Clear();
        _daySumPriceDt.Clear();
        _daySumDt.Clear();
        _dayHistory.Clear();
        _havePrev = false;

        foreach (var sid in _stocks.ById.Keys)
        {
            foreach (var l in _stocks.GetListings(sid))
            {
                if (l.SeedPrice <= 0m) continue;
                _seed[(sid, l.CurrencyType)] = l.SeedPrice;
            }
        }

        _lastTickUtc    = now;
        _windowStartUtc = _boundary == DayBoundaryMode.UtcMidnight
            ? new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc)
            : now;
        _logger.LogDebug("BotPriceMemoryService reset: {Count} (stock,ccy) seeded; boundary={Boundary}, halflife={H}s, day={D}s.",
            _seed.Count, _boundary, _halfLifeSec, _dayLengthSec);
    }

    /// <summary>
    /// Advance both the recent EWMA and the rolling daily-TWAP accumulator for every known
    /// (stock,currency), and rotate the day window when its length has elapsed. Single call per
    /// bot-loop iteration, loop-thread only, no RNG. When <c>anyConsumer=false</c> the method
    /// returns at the top — the byte-identical-when-off guarantee.
    /// </summary>
    internal void Tick(DateTime now)
    {
        if (!_anyConsumer) return;
        if (_lastTickUtc == DateTime.MaxValue) return; // not Reset-armed
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, MinDtSec, MaxDtSec);
        _lastTickUtc = now;

        foreach (var key in _seed.Keys)
        {
            var price = _priceLookup(key);
            if (price <= 0m) continue;

            // Recent EWMA — seed-fallback at the first observation so the EWMA opens at the
            // fresh price (not at 0) and doesn't take many ticks to warm up.
            if (_recent.TryGetValue(key, out var prevEwma) && prevEwma > 0m)
                _recent[key] = EwmaStep(prevEwma, price, dt, _halfLifeSec);
            else
                _recent[key] = price;

            // Day-TWAP accumulator: ∑ price·dt and ∑ dt across the in-progress window.
            _daySumPriceDt[key] = (_daySumPriceDt.TryGetValue(key, out var s) ? s : 0m) + price * (decimal)dt;
            _daySumDt[key]      = (_daySumDt.TryGetValue(key, out var t) ? t : 0m) + (decimal)dt;
        }

        // Day rotation check — at most one rotation per Tick. Missed days (loop paused) are
        // coalesced into a single rotation; not safety-critical to chase every missed boundary.
        bool rotate = _boundary == DayBoundaryMode.UtcMidnight
            ? now.Date > _windowStartUtc.Date
            : (now - _windowStartUtc).TotalSeconds >= _dayLengthSec;
        if (!rotate) return;

        foreach (var key in _seed.Keys)
        {
            var t = _daySumDt.TryGetValue(key, out var sumDt) ? sumDt : 0m;
            if (t > 0m && _daySumPriceDt.TryGetValue(key, out var sumPDt))
            {
                // §weighted-week: push this window's TWAP onto the rolling history. Tail = most
                // recent, head = oldest. Trim from the head when the queue exceeds WindowDays so
                // the weighted average only sees the configured window.
                var twap = sumPDt / t;
                if (!_dayHistory.TryGetValue(key, out var q)) { q = new Queue<decimal>(_windowDays); _dayHistory[key] = q; }
                q.Enqueue(twap);
                while (q.Count > _windowDays) q.Dequeue();
            }
            // else: no observation this window → don't push (would skew the average toward stale
            // values). Same intent as the prior "leave last good" behaviour.
            _daySumPriceDt[key] = 0m;
            _daySumDt[key] = 0m;
        }
        _havePrev = true;
        _windowStartUtc = _boundary == DayBoundaryMode.UtcMidnight
            ? new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc)
            : _windowStartUtc + TimeSpan.FromSeconds(_dayLengthSec);
    }

    /// <summary>
    /// Per-(stock,currency) short-EWMA price; the seed when no observation yet; 0 when unseeded.
    /// Mirrors <see cref="FundamentalService.Get"/>: missing key → 0, caller skip-on-<=0.
    /// </summary>
    internal decimal GetRecentEwma(int stockId, CurrencyType currency)
    {
        var key = (stockId, currency);
        if (_recent.TryGetValue(key, out var v) && v > 0m) return v;
        return _seed.TryGetValue(key, out var s) ? s : 0m;
    }

    /// <summary>
    /// §weighted-week: the linearly-tapered weighted average of the last <c>WindowDays</c> daily
    /// TWAPs, hard-clamped to <c>seed × [1 − MaxDailyDrift, 1 + MaxDailyDrift]</c>. The most-recent
    /// day weighs <c>WindowDays</c>, the second-most-recent <c>WindowDays-1</c>, …, the oldest one;
    /// any "missing" oldest slots (warmup, less than WindowDays of history) route their weight to
    /// seed so the day-0 behaviour stays byte-identical to a fixed-seed anchor. WindowDays=1
    /// reproduces the prior single-snapshot behaviour exactly. The clamp still applies — belt and
    /// suspenders against pathological pile-ups across the window.
    /// </summary>
    internal decimal GetPreviousDayAverage(int stockId, CurrencyType currency)
    {
        var key = (stockId, currency);
        if (!_seed.TryGetValue(key, out var seed) || seed <= 0m) return 0m;
        if (!_havePrev || !_dayHistory.TryGetValue(key, out var history) || history.Count == 0) return seed;
        var raw = WeightedAverage(history, _windowDays, seed);
        return ClampToBand(raw, seed, _maxDailyDrift);
    }

    /// <summary>
    /// One EWMA step with a half-life convention: α = 1 − 2^(−Δt / halfLifeSec). After
    /// <paramref name="halfLifeSec"/> elapses, half the weight has moved from the previous value
    /// to the fresh price. Δt ≤ 0 ⇒ no-op (returns <paramref name="prevEwma"/>). Pure &amp;
    /// RNG-free → unit-testable. Pattern mirrors <see cref="BotSentimentService.EwmaSlope"/>.
    /// </summary>
    internal static decimal EwmaStep(decimal prevEwma, decimal price, double dt, double halfLifeSec)
    {
        if (dt <= 0.0) return prevEwma;
        double hl = Math.Max(MinDtSec, halfLifeSec);
        double alpha = 1.0 - Math.Pow(2.0, -dt / hl);
        return prevEwma + (decimal)alpha * (price - prevEwma);
    }

    /// <summary>
    /// §weighted-week: linearly-tapered weighted average of <paramref name="history"/> over a
    /// notional window of <paramref name="windowDays"/> slots, with the most-recent entry weighing
    /// <c>windowDays</c> and slot <c>i</c> weighing <c>windowDays − i</c>. The history may be
    /// shorter than the window (warmup); the "missing" oldest slots route their weight to
    /// <paramref name="seed"/>, so day-0 with no history returns <paramref name="seed"/> exactly
    /// and day-1 with one entry blends in only <c>windowDays / Σ</c> of it. Pure &amp; RNG-free.
    /// </summary>
    internal static decimal WeightedAverage(IEnumerable<decimal> history, int windowDays, decimal seed)
    {
        if (windowDays < 1) windowDays = 1;
        // Snapshot once: Queue<T> doesn't index, and we walk it back-to-front to map "most recent"
        // → highest weight without depending on the concrete collection type at the call site.
        var arr = history as decimal[] ?? System.Linq.Enumerable.ToArray(history);
        int k = Math.Min(arr.Length, windowDays);
        int totalWeight = windowDays * (windowDays + 1) / 2;
        decimal weightedSum = 0m;
        // arr[Length-1] is the most recent → weight windowDays. arr[Length-1-i] → weight windowDays-i.
        for (int i = 0; i < k; i++)
            weightedSum += (windowDays - i) * arr[arr.Length - 1 - i];
        // Missing oldest slots k..windowDays-1 route their weight to seed.
        int seedWeight = 0;
        for (int i = k; i < windowDays; i++) seedWeight += windowDays - i;
        weightedSum += seedWeight * seed;
        return weightedSum / totalWeight;
    }

    /// <summary>
    /// Hard clamp to <c>seed × [1 − maxDrift, 1 + maxDrift]</c>. <paramref name="maxDrift"/> ≤ 0
    /// returns <paramref name="seed"/> exactly — the escape-hatch knob matching today's
    /// fixed-seed Fundamental behaviour.
    /// </summary>
    internal static decimal ClampToBand(decimal value, decimal seed, decimal maxDrift)
    {
        if (seed <= 0m) return value;
        if (maxDrift <= 0m) return seed;
        var d = Math.Clamp(maxDrift, 0m, 0.99m);
        var lo = seed * (1m - d);
        var hi = seed * (1m + d);
        return value < lo ? lo : value > hi ? hi : value;
    }
}
