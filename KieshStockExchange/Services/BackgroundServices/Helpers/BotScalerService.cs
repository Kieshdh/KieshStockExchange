using KieshStockExchange.Helpers;
using Microsoft.Extensions.Logging;
using KieshStockExchange.Services.BackgroundServices.Interfaces;

namespace KieshStockExchange.Services.BackgroundServices.Helpers;

/// <summary>
/// Clamped-proportional bot-count controller. Driven directly by AiTradeService
/// after each tick — no event subscription, no re-entrancy lock needed.
/// Holds its own tunables; AiTradeService re-exposes only what the dashboard binds.
/// </summary>
internal sealed class BotScalerService
{
    #region Tunables
    public bool Enabled { get; set; } = true;
    public int MinBotCap { get; set; } = 1;
    public double HighLoadFraction { get; set; } = 0.70;
    public double LowLoadFraction { get; set; } = 0.50;
    public double TargetLoadFraction { get; set; } = 0.60;
    public double MaxDeltaFraction { get; set; } = 0.25;
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int ConsecutiveSamples { get; set; } = 2;
    public TimeSpan CooldownAfterChange { get; set; } = TimeSpan.FromSeconds(4);
    #endregion

    #region Observed state
    public double LastLoadFraction { get; private set; }
    public int? LastTarget { get; private set; }
    #endregion

    #region Internal state
    private DateTime _lastSampleAt = DateTime.MinValue;
    private DateTime _lastChangeAt = DateTime.MinValue;
    private int _highCount;
    private int _lowCount;
    #endregion

    private readonly ILogger _logger;

    internal BotScalerService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Called from the bot loop after each tick. Returns the new ActiveBotCap to apply,
    /// or null if no change is warranted this tick.
    /// </summary>
    internal int? OnTick(IAiTradeService trade)
    {
        if (!Enabled) return null;
        if (!trade.LoopStartedAtUtc.HasValue) return null;

        var intervalMs = trade.TradeInterval.TotalMilliseconds;
        if (intervalMs <= 0) return null;

        var ewma = trade.TickWorkMsEwma;
        if (ewma <= 0) return null; // wait for the first real sample

        var now = TimeHelper.NowUtc();
        if (now - _lastSampleAt < SampleInterval) return null;
        _lastSampleAt = now;

        var loadFrac = ewma / intervalMs;
        LastLoadFraction = loadFrac;

        if (loadFrac >= HighLoadFraction) { _highCount++; _lowCount = 0; }
        else if (loadFrac <= LowLoadFraction) { _lowCount++; _highCount = 0; }
        else { _highCount = 0; _lowCount = 0; }

        if (now - _lastChangeAt < CooldownAfterChange) return null;

        int max = trade.MaxBotCap ?? int.MaxValue;
        int floor = Math.Max(0, MinBotCap);
        int current = trade.ActiveBotCap ?? max;

        int? target = null;
        if (_highCount >= ConsecutiveSamples && current > floor)
        {
            // Proportional: shrink so the loop's per-bot cost lands near TargetLoadFraction.
            var ratio = TargetLoadFraction / loadFrac;            // <1 when overloaded
            var desired = (int)Math.Floor(current * ratio);
            var maxDelta = Math.Max(1, (int)Math.Ceiling(current * MaxDeltaFraction));
            var clamped = Math.Max(current - maxDelta, desired);  // bound the cut
            target = Math.Max(floor, clamped);
        }
        else if (_lowCount >= ConsecutiveSamples && current < max)
        {
            int desired;
            if (current <= 0)
            {
                // Bootstrap out of zero: proportional math collapses, so step in by one.
                desired = 1;
            }
            else
            {
                var ratio = TargetLoadFraction / Math.Max(loadFrac, 0.01);  // >1 when light
                desired = (int)Math.Ceiling(current * ratio);
            }
            var basis = Math.Max(current, 1);
            var maxDelta = Math.Max(1, (int)Math.Ceiling(basis * MaxDeltaFraction));
            var clamped = Math.Min(current + maxDelta, desired);
            target = Math.Min(max, clamped);
        }

        if (!target.HasValue || target.Value == current) return null;

        _lastChangeAt = now;
        _highCount = _lowCount = 0;
        LastTarget = target;

        _logger.LogInformation(
            "Scaler: ActiveBotCap {Old}→{New} (load {Pct:P0} → target {Tgt:P0}, ewma {Ewma:F1}ms)",
            current, target.Value, loadFrac, TargetLoadFraction, ewma);

        return target;
    }
}
