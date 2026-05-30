using KieshStockExchange.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Multi-timescale shared market-mood state. Maintains independent
/// mean-reverting AR(1) factors at 24h / 1h / 10m / 1m scales — per-stock
/// at every scale, plus global factors at the 24h and 1h scales.
/// <see cref="GetSentiment"/> returns the un-clamped sum per stock so
/// callers can apply linear bias inside ±1 and react to the overflow
/// above ±1 with style-dependent market orders.
///
/// Driven by a single <see cref="Tick"/> call from the bot loop's
/// <c>CheckTimers</c>. Tick and GetSentiment both run on the loop thread,
/// so no locks are needed.
/// </summary>
internal sealed class BotSentimentService
{
    #region private constants
    // Amplitude per factor. Combined max ≈ ±2.6; the consumer clamps to ±1
    // for linear bias and uses the overflow for extreme-reaction market orders.
    private const decimal AmpPerStock24h = 0.60m;
    private const decimal AmpPerStock4h  = 0.50m;
    private const decimal AmpPerStock1h  = 0.40m;
    private const decimal AmpPerStock10m = 0.25m;
    private const decimal AmpPerStock1m  = 0.10m;
    private const decimal AmpGlobal24h   = 0.30m;
    private const decimal AmpGlobal4h    = 0.25m;
    private const decimal AmpGlobal1h    = 0.20m;

    // AR(1) mean-reversion: x_new = α·x_old + (1-α)·amp·U(-1,+1). Higher α
    // means slower mean reversion (0.7 keeps 70% of the prior value).
    private const decimal MeanReversionAlpha = 0.50m;

    // Deterministic seed so the simulation is reproducible across runs.
    private const int RngSeed = 43;

    // 50 stocks × 60s sampling × ~33 hours of runway. Each row is small.
    private const int RecentSamplesMax = 100_000;

    // News/earnings shocks: drop a decaying shock once it shrinks below this.
    private const decimal ShockFloor = 0.01m;
    #endregion

    #region Services and Constructor
    private readonly IStockService _stocks;
    private readonly ILogger<BotSentimentService> _logger;
    private readonly bool _newsEvents;
    private readonly decimal _shockMagnitude;
    private readonly decimal _shockDecayPerTick;
    private readonly double  _shockArrivalProbPerTick; // per stock per ~1s tick

    private readonly RingBufferStore<SentimentSample> _store;

    internal BotSentimentService(IStockService stocks, ILogger<BotSentimentService> logger,
        bool newsEvents = true, double shockMeanIntervalHours = 6.0,
        decimal shockMagnitude = 1.3m, decimal shockDecayPerTick = 0.999m)
    {
        _stocks = stocks ?? throw new ArgumentNullException(nameof(stocks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _newsEvents = newsEvents;
        _shockMagnitude    = shockMagnitude;
        _shockDecayPerTick = shockDecayPerTick;
        _shockArrivalProbPerTick = 1.0 / (Math.Max(0.0001, shockMeanIntervalHours) * 3600.0);
        _store  = new RingBufferStore<SentimentSample>("data/telemetry/bot_sentiment.ndjson");

        var prior = _store.LoadTail(RecentSamplesMax);
        foreach (var s in prior) _samples.Enqueue(s);
        if (prior.Count > 0)
            _logger.LogInformation("BotSentimentService: replayed {Count} sample(s) from disk.", prior.Count);

        // Default to "never reroll" until Reset(now) is called; until then
        // GetSentiment returns 0 (factor dictionaries empty). AiTradeService
        // calls Reset before the bot loop starts.
        _next1m = _next10m = _next1h = _next4h = _next24h = DateTime.MaxValue;
    }
    #endregion

    #region State
    private readonly Dictionary<int, decimal> _perStock24h = new();
    private readonly Dictionary<int, decimal> _perStock4h  = new();
    private readonly Dictionary<int, decimal> _perStock1h  = new();
    private readonly Dictionary<int, decimal> _perStock10m = new();
    private readonly Dictionary<int, decimal> _perStock1m  = new();
    private decimal _global24h;
    private decimal _global4h;
    private decimal _global1h;

    // Per-stock transient news shock; non-zero only while an event decays.
    private readonly Dictionary<int, decimal> _shock = new();

    private DateTime _next1m;
    private DateTime _next10m;
    private DateTime _next1h;
    private DateTime _next4h;
    private DateTime _next24h;

    private Random _rng = new(RngSeed);

    private readonly Queue<SentimentSample> _samples = new();
    #endregion

    #region Tick
    /// <summary>
    /// Re-roll any factors whose clock has expired. Called every bot-loop
    /// iteration; cheap when nothing has expired (four timestamp compares).
    /// </summary>
    internal void Tick(DateTime now)
    {
        bool rolled1m = false, rolled10m = false, rolled1h = false, rolled24h = false;

        if (now >= _next1m)
        {
            RerollPerStock(_perStock1m, AmpPerStock1m);
            _next1m = now + TimeSpan.FromMinutes(1);
            rolled1m = true;
        }
        if (now >= _next10m)
        {
            RerollPerStock(_perStock10m, AmpPerStock10m);
            _next10m = now + TimeSpan.FromMinutes(10);
            rolled10m = true;
        }
        if (now >= _next1h)
        {
            RerollPerStock(_perStock1h, AmpPerStock1h);
            _global1h = Step(_global1h, AmpGlobal1h);
            _next1h = now + TimeSpan.FromHours(1);
            rolled1h = true;
        }
        if (now >= _next4h)
        {
            RerollPerStock(_perStock4h, AmpPerStock4h);
            _global4h = Step(_global4h, AmpGlobal4h);
            _next4h = now + TimeSpan.FromHours(4);
        }
        if (now >= _next24h)
        {
            RerollPerStock(_perStock24h, AmpPerStock24h);
            _global24h = Step(_global24h, AmpGlobal24h);
            _next24h = now + TimeSpan.FromHours(24);
            rolled24h = true;
        }

        if (_newsEvents) StepShocks(now);

        // One combined snapshot per minute (driven by the 1m reroll clock).
        if (rolled1m) LogCombinedSentiment(now);
    }

    /// <summary>
    /// Decay any active news shocks, then roll a low-rate Poisson arrival per stock.
    /// A fired shock jumps the stock's sentiment past ±1 (sign random) and fades over
    /// minutes. Rolls advance <see cref="_rng"/> only when news events are enabled, so
    /// the disabled path leaves the AR(1) sequence bit-for-bit unchanged.
    /// </summary>
    private void StepShocks(DateTime now)
    {
        if (_shock.Count > 0)
        {
            foreach (var sid in _shock.Keys.ToList())
            {
                var v = _shock[sid] * _shockDecayPerTick;
                if (Math.Abs(v) < ShockFloor) _shock.Remove(sid);
                else _shock[sid] = v;
            }
        }

        foreach (var sid in _stocks.ById.Keys)
        {
            if (_rng.NextDouble() >= _shockArrivalProbPerTick) continue;
            var sign = _rng.NextDouble() < 0.5 ? -1m : 1m;
            _shock.TryGetValue(sid, out var cur);
            _shock[sid] = cur + sign * _shockMagnitude;
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var sym = _stocks.TryGetSymbol(sid, out var s) ? s : sid.ToString();
                _logger.LogInformation("News shock: {Symbol} {Delta:+0.0;-0.0}",
                    sym, sign * _shockMagnitude);
            }
        }
    }

    /// <summary>
    /// One combined snapshot per minute: the two global moods in the header, then the
    /// per-stock COMBINED sentiment (what bots act on — per-stock scales + globals +
    /// any news shock), 13 stocks per line. Per-bot personal sentiment is added per bot
    /// downstream and is not shown here.
    /// </summary>
    private void LogCombinedSentiment(DateTime now)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        const int PerLine = 13;
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder(512);
        sb.Append("Sentiment @ ").Append(now.ToLocalTime().ToString("HH:mm:ss"))
          .Append(" G24=").Append(_global24h.ToString("+0.00;-0.00;0.00", inv))
          .Append(" G4=").Append(_global4h.ToString("+0.00;-0.00;0.00", inv))
          .Append(" G1h=").Append(_global1h.ToString("+0.00;-0.00;0.00", inv))
          .Append(" |");

        int onThisLine = 0;
        foreach (var sid in _stocks.ById.Keys)
        {
            if (onThisLine == PerLine) { sb.Append('\n'); onThisLine = 0; }
            var symbol = _stocks.TryGetSymbol(sid, out var s) ? s : sid.ToString();
            sb.Append(' ').Append(symbol).Append(':')
              .Append(GetSentiment(sid).ToString("+0.00;-0.00;0.00", inv));
            onThisLine++;
        }
        _logger.LogInformation("{Line}", sb.ToString());
    }

    /// <summary>
    /// Combined sentiment for <paramref name="stockId"/>. Sum of per-stock
    /// factors (24h / 4h / 1h / 10m / 1m), the global 24h / 4h / 1h moods, and any
    /// active transient news shock.
    /// Returned UN-CLAMPED — typical range is ±1 but can reach ±2.1 (more
    /// during a news shock, which is intentional — it trips the extreme path).
    /// Callers clamp to ±1 for linear bias; the overflow drives v2's
    /// style-dependent extreme-reaction market orders.
    /// </summary>
    internal decimal GetSentiment(int stockId)
    {
        decimal sum = _global24h + _global4h + _global1h;
        if (_perStock24h.TryGetValue(stockId, out var v24)) sum += v24;
        if (_perStock4h.TryGetValue(stockId,  out var v4h)) sum += v4h;
        if (_perStock1h.TryGetValue(stockId,  out var v1h)) sum += v1h;
        if (_perStock10m.TryGetValue(stockId, out var v10)) sum += v10;
        if (_perStock1m.TryGetValue(stockId,  out var v1m)) sum += v1m;
        if (_shock.TryGetValue(stockId,       out var vsh)) sum += vsh;
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
        // x_new = α·x + (1-α)·amp·U(-1, +1).
        // α = Perc of prev value, x = prev value, amp = amplitude of change
        // U(-1, +1) = uniform random value between -1 and 1
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
        _perStock4h.Clear();
        _perStock1h.Clear();
        _perStock10m.Clear();
        _perStock1m.Clear();
        _shock.Clear();
        lock (_samples) _samples.Clear();

        foreach (var sid in _stocks.ById.Keys)
        {
            _perStock24h[sid] = SteadyState(AmpPerStock24h);
            _perStock4h[sid]  = SteadyState(AmpPerStock4h);
            _perStock1h[sid]  = SteadyState(AmpPerStock1h);
            _perStock10m[sid] = SteadyState(AmpPerStock10m);
            _perStock1m[sid]  = SteadyState(AmpPerStock1m);
        }
        _global24h = SteadyState(AmpGlobal24h);
        _global4h  = SteadyState(AmpGlobal4h);
        _global1h  = SteadyState(AmpGlobal1h);

        _next1m  = now + TimeSpan.FromMinutes(1);
        _next10m = now + TimeSpan.FromMinutes(10);
        _next1h  = now + TimeSpan.FromHours(1);
        _next4h  = now + TimeSpan.FromHours(4);
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

    #region Snapshot
    /// <summary>
    /// Append one row per known stock to the export ring, capturing the current
    /// factor values across all four timescales plus the two global moods.
    /// Drives the sentiment CSV that the Bot Dashboard exports.
    /// </summary>
    internal void LogSnapshot()
    {
        var now = TimeHelper.NowUtc();
        lock (_samples)
        {
            foreach (var sid in _stocks.ById.Keys)
            {
                _perStock24h.TryGetValue(sid, out var v24);
                _perStock4h.TryGetValue(sid,  out var v4h);
                _perStock1h.TryGetValue(sid,  out var v1h);
                _perStock10m.TryGetValue(sid, out var v10);
                _perStock1m.TryGetValue(sid,  out var v1m);
                _shock.TryGetValue(sid,       out var vsh);

                var sample = new SentimentSample(
                    TimestampUtc: now,
                    StockId:      sid,
                    PerStock24h:  v24,
                    PerStock4h:   v4h,
                    PerStock1h:   v1h,
                    PerStock10m:  v10,
                    PerStock1m:   v1m,
                    Global24h:    _global24h,
                    Global4h:     _global4h,
                    Global1h:     _global1h,
                    Shock:        vsh);
                _samples.Enqueue(sample);
                _store.Append(sample);
            }
            while (_samples.Count > RecentSamplesMax) _samples.Dequeue();
        }
    }
    #endregion

    #region Export
    internal int SampleCount
    {
        get { lock (_samples) return _samples.Count; }
    }

    internal string SuggestedExportFileName =>
        $"bot_sentiment_{TimeHelper.NowUtc():yyyyMMdd_HHmmss}";

    internal string BuildCsv(CancellationToken ct = default)
    {
        SentimentSample[] snapshot;
        lock (_samples) snapshot = _samples.ToArray();

        var sb = new StringBuilder(512 + snapshot.Length * 96);
        sb.AppendLine("TimestampUtc,StockId,PerStock24h,PerStock4h,PerStock1h,PerStock10m,PerStock1m," +
                      "Global24h,Global4h,Global1h,Shock,Combined");
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = snapshot[i];
            var combined = r.PerStock24h + r.PerStock4h + r.PerStock1h + r.PerStock10m + r.PerStock1m
                         + r.Global24h + r.Global4h + r.Global1h + r.Shock;
            sb.Append(r.TimestampUtc.ToString("O", inv)).Append(',')
              .Append(r.StockId).Append(',')
              .Append(r.PerStock24h.ToString(inv)).Append(',')
              .Append(r.PerStock4h.ToString(inv)).Append(',')
              .Append(r.PerStock1h.ToString(inv)).Append(',')
              .Append(r.PerStock10m.ToString(inv)).Append(',')
              .Append(r.PerStock1m.ToString(inv)).Append(',')
              .Append(r.Global24h.ToString(inv)).Append(',')
              .Append(r.Global4h.ToString(inv)).Append(',')
              .Append(r.Global1h.ToString(inv)).Append(',')
              .Append(r.Shock.ToString(inv)).Append(',')
              .Append(combined.ToString(inv))
              .Append('\n');
        }

        return sb.ToString();
    }

    internal async Task<string> ExportCsvAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Export path is required.", nameof(path));
        await File.WriteAllTextAsync(path, BuildCsv(ct), ct).ConfigureAwait(false);
        _logger.LogInformation("Exported bot sentiment rows to {Path}.", path);
        return path;
    }
    #endregion
}

internal readonly record struct SentimentSample(
    DateTime TimestampUtc,
    int      StockId,
    decimal  PerStock24h,
    decimal  PerStock4h,
    decimal  PerStock1h,
    decimal  PerStock10m,
    decimal  PerStock1m,
    decimal  Global24h,
    decimal  Global4h,
    decimal  Global1h,
    decimal  Shock);
