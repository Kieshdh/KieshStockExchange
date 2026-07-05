using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.MarketDataServices;

/// <summary> AR(1) mid-rate walker with a fixed spread. Deterministic across runs. </summary>
public sealed class FxRateService : IFxRateService
{
    #region Tunables (defaults mirror Tools/Config.py FX_* constants; flip via Bots:Fx:* — see Configure)
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    // x_new = Alpha*x_old + (1-Alpha)*base + Amplitude*base*U(-1,+1), clamped to base*(1 +/- RateBand).
    // Defaults reproduce the historical consts byte-for-byte when Bots:Fx:* is unset. Damped arm =
    // higher Alpha (smoother/more random-walk) + lower Amplitude (less volatile) + tighter RateBand.
    private static decimal _alpha = 0.92m;
    private static decimal _amplitude = 0.005m;
    private static decimal _convertSpread = 0.001m;  // 0.2% round-trip
    private static decimal _rateBand = 0.20m;        // clamp to base +/- this fraction
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

    #region Config (Bots:Fx:* — read once at startup, before the singleton is built)
    /// <summary>
    /// Wired from server startup (Program.cs). Reads Bots:Fx:Alpha/Amplitude/ConvertSpread/RateBand;
    /// defaults = the historical constants ⇒ byte-identical when the section is absent. Raising Alpha
    /// smooths the wander (more random-walk), lowering Amplitude cuts per-tick vol, a tighter RateBand
    /// narrows the bound — together a mean-reverting bounded walk at ~1% intraday vol.
    /// </summary>
    public static void Configure(IConfiguration config)
    {
        _alpha = config.GetValue("Bots:Fx:Alpha", _alpha);
        _amplitude = config.GetValue("Bots:Fx:Amplitude", _amplitude);
        _convertSpread = config.GetValue("Bots:Fx:ConvertSpread", _convertSpread);
        _rateBand = config.GetValue("Bots:Fx:RateBand", _rateBand);
    }

    /// <summary>Test seam — set the tunables without IConfiguration. Reset to defaults in teardown.</summary>
    public static void ConfigureForTests(decimal alpha, decimal amplitude, decimal convertSpread, decimal rateBand)
    {
        _alpha = alpha;
        _amplitude = amplitude;
        _convertSpread = convertSpread;
        _rateBand = rateBand;
    }
    #endregion

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
        return (mid * (1m - _convertSpread), mid * (1m + _convertSpread));
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
        var raw = _alpha * prev + (1m - _alpha) * baseMid + _amplitude * baseMid * noise;

        var lo = baseMid * (1m - _rateBand);
        var hi = baseMid * (1m + _rateBand);
        if (raw < lo) raw = lo;
        if (raw > hi) raw = hi;
        return raw;
    }
    #endregion
}
