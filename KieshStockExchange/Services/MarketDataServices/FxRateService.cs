using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary> AR(1) mid-rate walker with a fixed spread. Deterministic across runs. </summary>
public sealed class FxRateService : IFxRateService
{
    #region Tunables (mirror Tools/Config.py FX_* constants)
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    // x_new = Alpha*x_old + (1-Alpha)*base + Amplitude*base*U(-1,+1)
    private const decimal Alpha = 0.92m;
    private const decimal Amplitude = 0.005m;
    private const decimal ConvertSpread = 0.001m;  // 0.2% round-trip
    private const decimal RateBand = 0.20m;        // clamp to base ± 20%
    private const int RngSeed = 47;
    #endregion

    #region Base rates (mirror Tools/Config.py::FX_BASE_RATES)
    // Only one direction per pair; the reverse is 1 / mid.
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
        // Cold path before Reset or before a pair is loaded.
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
        var noise = (decimal)(_rng.NextDouble() * 2.0 - 1.0);
        var raw = Alpha * prev + (1m - Alpha) * baseMid + Amplitude * baseMid * noise;

        var lo = baseMid * (1m - RateBand);
        var hi = baseMid * (1m + RateBand);
        if (raw < lo) raw = lo;
        if (raw > hi) raw = hi;
        return raw;
    }
    #endregion
}
