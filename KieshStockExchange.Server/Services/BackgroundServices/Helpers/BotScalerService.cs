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

    // §B control-loop levers — all default-off ⇒ loadFrac == ewma/intervalMs, byte-identical to prior.
    // §B-1a: divide by the TRUE wall-clock period (intervalMs + full tick work) instead of the bare
    // interval — the loop's fixed Task.Delay(TradeInterval) makes the real period 1000 + ewma, so the
    // uncorrected ewma/1000 overstates load and holds the cap far below what the box can carry.
    public bool CorrectDutyCycleDenominator { get; set; } = false;
    // §B-2: size the cap from the actionable (Collect+Batch) span the scaler can act on, rather than the
    // full tick span that also includes the cap-exempt cohorts (arb/mm/rotator/jump/drain).
    public bool SizeFromActionableSpan { get; set; } = false;
    // §B-P-a tick-≤-interval guard: refuse a cap INCREASE once the FULL tick already fills this fraction
    // of the true period, so correcting the denominator (§B-1a) can't crank the cap up until the tick
    // work balloons past the interval and the EWMA chases itself. Default 1.0 ⇒ fullDuty (strictly < 1
    // for any finite ewma) never reaches it ⇒ byte-identical; wiring lowers it to ~0.95 only when the
    // denominator correction is enabled.
    public double TickGuardFraction { get; set; } = 1.0;
    #endregion

    #region Observed state
    public double LastLoadFraction { get; private set; }
    // §B-3: EWMA-smoothed load, published for the rotator's opt-in read (a smoothed alternative to the
    // raw 2s sample it reads today). Always maintained; does not feed the cap math ⇒ byte-identical.
    public double LoadFractionEwma { get; private set; }
    public int? LastTarget { get; private set; }
    #endregion

    #region Internal state
    private DateTime _lastSampleAt = DateTime.MinValue;
    private DateTime _lastChangeAt = DateTime.MinValue;
    private int _highCount;
    private int _lowCount;
    // §B-3: state for the published LoadFractionEwma (smoothed at ~3 samples).
    private double _loadFractionEwma;
    private const double LoadEwmaAlpha = 0.3;

    // Logging throttle. Cap changes happen frequently when load wobbles around a
    // threshold; emitting one INFO per change buries the rest of the log. Buffer
    // changes here and emit a summary at most every LogInterval.
    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(60);
    private DateTime _lastLogAt = DateTime.MinValue;
    private readonly List<(int Old, int New, double Load, double Ewma)> _pendingChanges = new();
    #endregion

    #region Services and Constructor
    private readonly ILogger<BotScalerService> _logger;

    internal BotScalerService(ILogger<BotScalerService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    #endregion

    #region Tick
    /// <summary>
    /// Called from the bot loop after each tick. Returns the new ActiveBotCap to apply,
    /// or null if no change is warranted this tick.
    /// </summary>
    internal int? OnTick(IAiTradeService trade)
    {
        if (!Enabled) return null;
        if (!trade.LoopStartedAtUtc.HasValue) return null;

        // Even on a no-change tick, flush a buffered summary if the throttle
        // window has elapsed — otherwise rapid bursts followed by quiet would
        // leave the last summary stuck in the buffer indefinitely.
        FlushPendingLog(TimeHelper.NowUtc(), force: false);

        var intervalMs = trade.TradeInterval.TotalMilliseconds;
        if (intervalMs <= 0) return null;

        var fullEwma = trade.TickWorkMsEwma;
        if (fullEwma <= 0) return null; // wait for the first real sample

        var now = TimeHelper.NowUtc();
        if (now - _lastSampleAt < SampleInterval) return null;
        _lastSampleAt = now;

        // §B-2 sizing span + §B-1a denominator. Both flags off ⇒ sizeEwma == fullEwma and
        // denom == intervalMs ⇒ loadFrac == fullEwma/intervalMs (today, byte-identical).
        var sizeEwma = SizeFromActionableSpan ? trade.TickWorkActionableMsEwma : fullEwma;
        if (sizeEwma <= 0.0) sizeEwma = fullEwma; // actionable span not warm yet ⇒ fall back to full
        var denom = CorrectDutyCycleDenominator ? (intervalMs + fullEwma) : intervalMs;
        var loadFrac = sizeEwma / denom;
        LastLoadFraction = loadFrac;
        _loadFractionEwma = _loadFractionEwma <= 0.0
            ? loadFrac
            : LoadEwmaAlpha * loadFrac + (1.0 - LoadEwmaAlpha) * _loadFractionEwma;
        LoadFractionEwma = _loadFractionEwma;

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
            // §B-P-a tick-≤-interval guard: never raise the cap when the FULL tick already fills most of
            // the true period, regardless of which span sized loadFrac. Uses the real wall-clock duty
            // cycle (fullEwma / (intervalMs + fullEwma)), so it binds even under the §B-1a correction.
            // Guard fraction 1.0 ⇒ never triggers ⇒ byte-identical to prior increase behaviour.
            var fullDuty = fullEwma / (intervalMs + fullEwma);
            if (fullDuty >= TickGuardFraction) return null;

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

        _pendingChanges.Add((current, target.Value, loadFrac, fullEwma));
        FlushPendingLog(now, force: false);

        return target;
    }
    #endregion

    #region Logging
    /// <summary>
    /// Emit a buffered summary of cap changes if the throttle window has elapsed
    /// (or unconditionally when <paramref name="force"/> is true). One INFO line
    /// per window — single change reads as before; multiple changes collapse to
    /// "first→last via N steps" with the load range across the window.
    /// </summary>
    private void FlushPendingLog(DateTime now, bool force)
    {
        if (_pendingChanges.Count == 0) return;
        if (!force && (now - _lastLogAt) < LogInterval) return;

        // Both shapes expose the SAME aggregatable numeric props — Cap (resulting cap), LoadPct (peak
        // load, with LoadMin for the range), Ewma — so the telemetry sink forwards them and the web
        // viewer can range-aggregate (cap min→max, load lo–hi) a time bucket uniformly across either line.
        if (_pendingChanges.Count == 1)
        {
            var c = _pendingChanges[0];
            _logger.LogInformation(
                "Scaler: ActiveBotCap {Old}→{Cap} (load {LoadPct:0%} → target {Tgt:0%}, ewma {Ewma:F1}ms)",
                c.Old, c.New, c.Load, TargetLoadFraction, c.Ewma);
        }
        else
        {
            var first = _pendingChanges[0];
            var last = _pendingChanges[^1];
            double minLoad = first.Load, maxLoad = first.Load, lastEwma = first.Ewma;
            for (int i = 1; i < _pendingChanges.Count; i++)
            {
                var c = _pendingChanges[i];
                if (c.Load < minLoad) minLoad = c.Load;
                if (c.Load > maxLoad) maxLoad = c.Load;
                lastEwma = c.Ewma;
            }
            _logger.LogInformation(
                "Scaler: ActiveBotCap {First}→{Cap} via {Count} steps in {Secs:F0}s (load {LoadMin:0%}–{LoadPct:0%} → target {Tgt:0%}, ewma {Ewma:F1}ms)",
                first.Old, last.New, _pendingChanges.Count,
                (now - _lastLogAt).TotalSeconds, minLoad, maxLoad, TargetLoadFraction, lastEwma);
        }

        _pendingChanges.Clear();
        _lastLogAt = now;
    }
    #endregion
}
