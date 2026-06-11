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

    // Per-(stock,currency) state. Plain Dictionary<> — single loop-thread mutator, same pattern
    // as FundamentalService._current / BotSentimentService._combined.
    private readonly Dictionary<(int, CurrencyType), decimal> _seed = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _recent = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _daySumPriceDt = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _daySumDt = new();
    private readonly Dictionary<(int, CurrencyType), decimal> _dayPrev = new();

    private bool _havePrev;
    private DateTime _lastTickUtc = DateTime.MaxValue; // MaxValue = inert until Reset
    private DateTime _windowStartUtc = DateTime.MaxValue;

    internal BotPriceMemoryService(IStockService stocks, ILogger<BotPriceMemoryService> logger,
        Func<(int, CurrencyType), decimal> priceLookup,
        bool anyConsumer = false,
        double halfLifeSec = 1800.0, double dayLengthHours = 24.0,
        DayBoundaryMode boundary = DayBoundaryMode.ServiceStart,
        decimal maxDailyDrift = 0.50m)
    {
        _stocks      = stocks      ?? throw new ArgumentNullException(nameof(stocks));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));
        _priceLookup = priceLookup ?? throw new ArgumentNullException(nameof(priceLookup));
        _anyConsumer  = anyConsumer;
        // Defensive sanity: halflife and dayLength must be > MinDtSec or the EWMA / rotation math
        // explodes; maxDrift < 1.0 so the lower band stays > 0.
        _halfLifeSec  = Math.Max(MinDtSec, halfLifeSec);
        _dayLengthSec = Math.Max(MinDtSec, dayLengthHours * 3600.0);
        _maxDailyDrift = Math.Clamp(maxDailyDrift, 0m, 0.99m);
        _boundary     = boundary;
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
        _dayPrev.Clear();
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
                _dayPrev[key] = sumPDt / t;
            // else: no observation this window → leave _dayPrev untouched (last good value, or
            // seed-fallback if there was never one).
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
    /// The previous day's TWAP, hard-clamped to <c>seed × [1 − MaxDailyDrift, 1 + MaxDailyDrift]</c>
    /// at READ time so the underlying mean still tracks reality for telemetry but the value
    /// consumed by the anchor is always bounded. Falls back to seed during warmup (before the
    /// first rotation) and for unseen keys — making day-0 byte-identical to a fixed-seed anchor.
    /// </summary>
    internal decimal GetPreviousDayAverage(int stockId, CurrencyType currency)
    {
        var key = (stockId, currency);
        if (!_seed.TryGetValue(key, out var seed) || seed <= 0m) return 0m;
        if (!_havePrev || !_dayPrev.TryGetValue(key, out var raw)) return seed;
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
