using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary>
/// AR(1) mid-rate walker with a fixed spread. Shape mirrors
/// <c>BotSentimentService</c>: single seeded RNG, deterministic across
/// runs, all state lives in memory. <see cref="Tick"/> runs on the bot
/// loop thread so no locking is needed for reads from that thread; the
/// Convert preview reads through the same dictionary and accepts the
/// brief inconsistency window between mid update and event dispatch.
/// </summary>
public sealed class FxRateService : IFxRateService
{
    #region Tunables (mirror Tools/Config.py FX_* constants)
    // Re-roll cadence. Matches Tools/Config.py::FX_TICK_INTERVAL_SECONDS.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    // AR(1) momentum: x_new = alpha*x_old + (1-alpha)*base + amp*base*U(-1,+1).
    // Higher alpha → slower mean reversion (0.92 keeps 92% of the prior value).
    private const decimal Alpha = 0.92m;
    private const decimal Amplitude = 0.005m;

    // Convert spread: 0.001 = ±0.1% around mid = 0.2% round-trip cost.
    private const decimal ConvertSpread = 0.001m;

    // Hard clamp: mid stays within ±20% of the base rate so the AR(1)
    // can't run away under an unlucky string of draws.
    private const decimal RateBand = 0.20m;

    // Deterministic seed so reruns are reproducible.
    private const int RngSeed = 47;
    #endregion

    #region Base rates (mirror Tools/Config.py::FX_BASE_RATES)
    // Key is (from, to) reading "1 from = X to". Same shape as
    // CurrencyHelper.RatesPerBase but only carries the live pairs.
    // Reverse pairs are computed by GetMidRate as 1 / mid, so we only
    // store one direction per pair to avoid drift between the two sides.
    private static readonly IReadOnlyDictionary<(CurrencyType, CurrencyType), decimal> BaseMidRates =
        new Dictionary<(CurrencyType, CurrencyType), decimal>
        {
            { (CurrencyType.EUR, CurrencyType.USD), 1.08m },
        };
    #endregion

    #region State
    private readonly Dictionary<(CurrencyType, CurrencyType), decimal> _mids = new();
    private readonly Dictionary<(CurrencyType, CurrencyType), DateTime> _nextTick = new();
    private Random _rng = new(RngSeed);
    private readonly ILogger<FxRateService> _logger;

    public event EventHandler<FxRateUpdatedEventArgs>? RateUpdated;
    #endregion

    public FxRateService(ILogger<FxRateService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Reset();
    }

    #region Public API
    public decimal GetMidRate(CurrencyType from, CurrencyType to)
    {
        if (from == to) return 1m;
        if (_mids.TryGetValue((from, to), out var mid) && mid > 0m) return mid;
        if (_mids.TryGetValue((to, from), out var inverse) && inverse > 0m) return 1m / inverse;
        // Cold path before Reset / before a pair is loaded — fall back to the
        // static table so callers don't crash on a startup race.
        return CurrencyHelper.Convert(1m, from, to, decimals: 6);
    }

    public (decimal Bid, decimal Ask) GetBidAsk(CurrencyType from, CurrencyType to)
    {
        var mid = GetMidRate(from, to);
        return (mid * (1m - ConvertSpread), mid * (1m + ConvertSpread));
    }

    public void Reset()
    {
        _rng = new Random(RngSeed);
        _mids.Clear();
        _nextTick.Clear();
        var now = TimeHelper.NowUtc();
        foreach (var kv in BaseMidRates)
        {
            _mids[kv.Key] = kv.Value;
            _nextTick[kv.Key] = now + TickInterval;
        }
        _logger.LogDebug("FxRateService reset: {Pairs} pairs seeded.", _mids.Count);
    }

    public void Tick(DateTime now)
    {
        // Snapshot keys so the loop can mutate _mids in place without
        // CollectionModified surprises if a pair is added/removed mid-loop
        // (only Reset does that today, but stay defensive).
        var pairs = _nextTick.Keys.ToArray();
        foreach (var pair in pairs)
        {
            if (now < _nextTick[pair]) continue;

            var oldMid = _mids[pair];
            var baseMid = BaseMidRates[pair];
            var newMid = StepAr1(oldMid, baseMid);
            _mids[pair] = newMid;
            _nextTick[pair] = now + TickInterval;

            RateUpdated?.Invoke(this, new FxRateUpdatedEventArgs(pair.Item1, pair.Item2, oldMid, newMid));

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "FX {From}/{To}: {Old:0.######} -> {New:0.######} (base {Base:0.######})",
                    pair.Item1, pair.Item2, oldMid, newMid, baseMid);
        }
    }
    #endregion

    #region AR(1) step
    private decimal StepAr1(decimal prev, decimal baseMid)
    {
        // x_new = alpha*prev + (1-alpha)*base + amp*base*U(-1,+1)
        var noise = (decimal)(_rng.NextDouble() * 2.0 - 1.0);
        var raw = Alpha * prev + (1m - Alpha) * baseMid + Amplitude * baseMid * noise;

        // Clamp to base ± RateBand so an unlucky walk can't drift away.
        var lo = baseMid * (1m - RateBand);
        var hi = baseMid * (1m + RateBand);
        if (raw < lo) raw = lo;
        if (raw > hi) raw = hi;
        return raw;
    }
    #endregion
}
