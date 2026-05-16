using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Multi-timescale shared market-mood state. Maintains independent
/// mean-reverting AR(1) factors at 24h / 1h / 10m / 1m scales — per-stock
/// at every scale, plus global factors at the 24h and 1h scales. Returns
/// the un-clamped sum per stock so callers can apply linear bias inside
/// ±1 AND react to overflow above ±1 (style-dependent market orders;
/// see v2 plan).
///
/// Driven by a single <see cref="Tick"/> call from the bot loop's
/// <c>CheckTimers</c> — same per-scale clock pattern as the other periodic
/// jobs in <see cref="AiTradeService"/>. <see cref="GetSentiment"/> is
/// called from <c>AiBotDecisionService</c> (v2) on the same thread, so no
/// locks are needed.
/// </summary>
internal sealed class BotSentimentService
{
    #region Services and Constructor
    // Amplitude per factor (decimal). Combined max ≈ ±1.85; the consumer
    // clamps to ±1 for linear bias and uses the overflow for extreme-reaction
    // market orders (v2).
    private const decimal AmpPerStock24h = 0.60m;
    private const decimal AmpPerStock1h  = 0.40m;
    private const decimal AmpPerStock10m = 0.25m;
    private const decimal AmpPerStock1m  = 0.10m;
    private const decimal AmpGlobal24h   = 0.30m;
    private const decimal AmpGlobal1h    = 0.20m;

    // AR(1) mean-reversion fraction: x_new = α·x_old + (1-α)·amp·U(-1,+1).
    // 0.7 keeps 70% of the prior value each reroll — bounded random walk
    // that mean-reverts toward 0 over a few periods.
    private const decimal MeanReversionAlpha = 0.70m;

    // Deterministic seed so the simulation is reproducible across runs.
    // Matches the AiBotContext.DailySeed pattern.
    private const int RngSeed = 7919;

    private readonly IStockService _stocks;
    private readonly ILogger<BotSentimentService> _logger;

    internal BotSentimentService(IStockService stocks, ILogger<BotSentimentService> logger)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Default to "never reroll" until Reset(now) is called; until then
        // GetSentiment returns 0 (factor dictionaries empty). AiTradeService
        // calls Reset before the bot loop starts.
        _next1m = _next10m = _next1h = _next24h = DateTime.MaxValue;
    }
    #endregion

    #region State
    private readonly Dictionary<int, decimal> _perStock24h = new();
    private readonly Dictionary<int, decimal> _perStock1h  = new();
    private readonly Dictionary<int, decimal> _perStock10m = new();
    private readonly Dictionary<int, decimal> _perStock1m  = new();
    private decimal _global24h;
    private decimal _global1h;

    private DateTime _next1m;
    private DateTime _next10m;
    private DateTime _next1h;
    private DateTime _next24h;

    private Random _rng = new(RngSeed);
    #endregion

    #region Tick
    /// <summary>
    /// Re-roll any factors whose clock has expired. Called every bot-loop
    /// iteration; cheap when nothing has expired (four timestamp compares).
    /// </summary>
    internal void Tick(DateTime now)
    {
        if (now >= _next1m)
        {
            RerollPerStock(_perStock1m, AmpPerStock1m);
            _next1m = now + TimeSpan.FromMinutes(1);
        }
        if (now >= _next10m)
        {
            RerollPerStock(_perStock10m, AmpPerStock10m);
            _next10m = now + TimeSpan.FromMinutes(10);
        }
        if (now >= _next1h)
        {
            RerollPerStock(_perStock1h, AmpPerStock1h);
            _global1h = Step(_global1h, AmpGlobal1h);
            _next1h = now + TimeSpan.FromHours(1);
        }
        if (now >= _next24h)
        {
            RerollPerStock(_perStock24h, AmpPerStock24h);
            _global24h = Step(_global24h, AmpGlobal24h);
            _next24h = now + TimeSpan.FromHours(24);
        }
    }

    /// <summary>
    /// Combined sentiment for <paramref name="stockId"/>. Sum of per-stock
    /// factors (24h / 1h / 10m / 1m) plus the global 24h and 1h moods.
    /// Returned UN-CLAMPED — typical range is ±1 but can reach ±1.85.
    /// Callers clamp to ±1 for linear bias; the overflow drives v2's
    /// style-dependent extreme-reaction market orders.
    /// </summary>
    internal decimal GetSentiment(int stockId)
    {
        decimal sum = _global24h + _global1h;
        if (_perStock24h.TryGetValue(stockId, out var v24)) sum += v24;
        if (_perStock1h.TryGetValue(stockId,  out var v1h)) sum += v1h;
        if (_perStock10m.TryGetValue(stockId, out var v10)) sum += v10;
        if (_perStock1m.TryGetValue(stockId,  out var v1m)) sum += v1m;
        return sum;
    }
    #endregion

    #region Reroll
    private void RerollPerStock(Dictionary<int, decimal> factors, decimal amp)
    {
        foreach (var sid in _stocks.ById.Keys)
        {
            factors.TryGetValue(sid, out var prev);
            factors[sid] = Step(prev, amp);
        }
    }

    private decimal Step(decimal prev, decimal amp)
    {
        // AR(1) with bounded innovation: x_new = α·x + (1-α)·amp·U(-1, +1).
        // Cast through double for the random draw — Random has no direct
        // decimal API and the loss of precision is irrelevant here.
        var noise = (decimal)(_rng.NextDouble() * 2.0 - 1.0);
        return MeanReversionAlpha * prev + (1m - MeanReversionAlpha) * amp * noise;
    }
    #endregion

    #region Reset
    /// <summary>
    /// Re-seed all factors from their steady-state distribution
    /// (uniform on `[-amp, +amp]`) and schedule the first reroll on each
    /// scale one period from <paramref name="now"/>. Seeding avoids the
    /// "calm start" artifact where sentiment is 0 for the first minute /
    /// hour / day after a session start.
    /// </summary>
    internal void Reset(DateTime now)
    {
        _rng = new Random(RngSeed);
        _perStock24h.Clear();
        _perStock1h.Clear();
        _perStock10m.Clear();
        _perStock1m.Clear();

        foreach (var sid in _stocks.ById.Keys)
        {
            _perStock24h[sid] = SteadyState(AmpPerStock24h);
            _perStock1h[sid]  = SteadyState(AmpPerStock1h);
            _perStock10m[sid] = SteadyState(AmpPerStock10m);
            _perStock1m[sid]  = SteadyState(AmpPerStock1m);
        }
        _global24h = SteadyState(AmpGlobal24h);
        _global1h  = SteadyState(AmpGlobal1h);

        _next1m  = now + TimeSpan.FromMinutes(1);
        _next10m = now + TimeSpan.FromMinutes(10);
        _next1h  = now + TimeSpan.FromHours(1);
        _next24h = now + TimeSpan.FromHours(24);

        _logger.LogDebug("BotSentimentService reset: {Stocks} stocks seeded across 4 scales.",
            _stocks.ById.Count);
    }

    private decimal SteadyState(decimal amp)
    {
        var u = (decimal)(_rng.NextDouble() * 2.0 - 1.0);
        return amp * u;
    }
    #endregion
}
