using KieshStockExchange.Helpers;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §B scaler control-loop unit tests. <see cref="BotScalerService.OnTick"/> is pure control math over
/// (ewma, actionable-ewma, interval, ActiveBotCap) plus the wall-clock sample/cooldown gates, so it is
/// exercised as a pure function here — the clock is driven via <see cref="TimeHelper.NowUtc"/> and the
/// tick state via a Moq <see cref="IAiTradeService"/>. These assert the §B levers behave as designed AND
/// that with every lever OFF the cap decision is exactly today's (byte-identical control path), which the
/// 430-suite cannot show because it never instantiates the scaler.
/// </summary>
[Collection("ClockSerial")]
public sealed class BotScalerOnTickTests : IDisposable
{
    // §B levers are wall-clock/EWMA driven, NOT part of seed replay; the clock is a process-static Func,
    // so save + restore it around each test (xUnit runs a class's tests serially).
    private readonly Func<DateTime> _savedClock = TimeHelper.NowUtc;
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public BotScalerOnTickTests() => TimeHelper.NowUtc = () => _now;
    public void Dispose() => TimeHelper.NowUtc = _savedClock;

    private static BotScalerService NewScaler() => new(NullLogger<BotScalerService>.Instance);

    private static Mock<IAiTradeService> Trade(double fullEwmaMs, int activeCap,
        double actionableEwmaMs = 0.0, int maxCap = 20000, double intervalMs = 1000.0)
    {
        var m = new Mock<IAiTradeService>(MockBehavior.Loose);
        m.SetupGet(t => t.LoopStartedAtUtc).Returns(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        m.SetupGet(t => t.TradeInterval).Returns(TimeSpan.FromMilliseconds(intervalMs));
        m.SetupGet(t => t.TickWorkMsEwma).Returns(fullEwmaMs);
        m.SetupGet(t => t.TickWorkActionableMsEwma).Returns(actionableEwmaMs);
        m.SetupGet(t => t.ActiveBotCap).Returns((int?)activeCap);
        m.SetupGet(t => t.MaxBotCap).Returns((int?)maxCap);
        return m;
    }

    // A cap decision needs two in-band samples ConsecutiveSamples(2) apart, each past SampleInterval(2s).
    private int? DriveToDecision(BotScalerService s, IAiTradeService trade)
    {
        s.OnTick(trade);              // sample 1 → count = 1, no change yet
        _now = _now.AddSeconds(2.5);  // clear the 2s SampleInterval gate
        return s.OnTick(trade);       // sample 2 → count = 2 → decision (cooldown clear: never changed)
    }

    [Fact]
    public void AllLeversOff_isTodaysPath_highLoadShrinks_lowLoadGrows()
    {
        // fullEwma 800ms / interval 1000 ⇒ loadFrac 0.8 ≥ HighLoadFraction(0.70) ⇒ shrink below current.
        var shrink = DriveToDecision(NewScaler(), Trade(fullEwmaMs: 800, activeCap: 1000).Object);
        Assert.NotNull(shrink);
        Assert.True(shrink < 1000, $"expected shrink below 1000, got {shrink}");

        // fullEwma 300ms ⇒ loadFrac 0.3 ≤ LowLoadFraction(0.50) ⇒ grow above current, capped at MaxBotCap.
        var grow = DriveToDecision(NewScaler(), Trade(fullEwmaMs: 300, activeCap: 100).Object);
        Assert.NotNull(grow);
        Assert.True(grow > 100, $"expected grow above 100, got {grow}");

        // fullEwma 600ms ⇒ loadFrac 0.6, inside the [0.50, 0.70] deadband ⇒ no change (both samples null).
        Assert.Null(DriveToDecision(NewScaler(), Trade(fullEwmaMs: 600, activeCap: 500).Object));
    }

    [Fact]
    public void DutyCycleDenominator_correctsTheDutyCycle_andReleasesTheCap()
    {
        // fullEwma 600ms. Uncorrected: 600/1000 = 0.6 (deadband) ⇒ no change.
        Assert.Null(DriveToDecision(NewScaler(), Trade(fullEwmaMs: 600, activeCap: 100).Object));

        // Corrected: 600/(1000+600) = 0.375 ≤ 0.50 ⇒ the box is really underloaded ⇒ the cap grows.
        var s = NewScaler();
        s.CorrectDutyCycleDenominator = true;
        var grow = DriveToDecision(s, Trade(fullEwmaMs: 600, activeCap: 100).Object);
        Assert.NotNull(grow);
        Assert.True(grow > 100, $"expected corrected duty-cycle to grow the cap, got {grow}");
    }

    [Fact]
    public void TickGuard_refusesACapIncrease_whenTheFullTickNearlyFillsThePeriod()
    {
        // Actionable span says "grow" (100/2600 ≈ 0.04) but the FULL tick fills 1600/2600 ≈ 0.615 of the
        // true period. Guard 0.60 ⇒ the increase is refused; guard 1.0 (inert) ⇒ the increase proceeds.
        Mock<IAiTradeService> T() => Trade(fullEwmaMs: 1600, activeCap: 100, actionableEwmaMs: 100);

        var guarded = NewScaler();
        guarded.CorrectDutyCycleDenominator = true;
        guarded.SizeFromActionableSpan = true;
        guarded.TickGuardFraction = 0.60;
        Assert.Null(DriveToDecision(guarded, T().Object));

        var unguarded = NewScaler();
        unguarded.CorrectDutyCycleDenominator = true;
        unguarded.SizeFromActionableSpan = true;
        unguarded.TickGuardFraction = 1.0; // strictly unreachable ⇒ never blocks
        var grow = DriveToDecision(unguarded, T().Object);
        Assert.NotNull(grow);
        Assert.True(grow > 100, $"expected the unguarded scaler to grow, got {grow}");
    }

    [Fact]
    public void ActionableSpanSizing_sizesFromCollectBatch_notTheCapExemptCohorts()
    {
        // Full tick 800ms (cohorts included) reads as high load ⇒ default sizing SHRINKS.
        var shrink = DriveToDecision(NewScaler(), Trade(fullEwmaMs: 800, activeCap: 1000, actionableEwmaMs: 300).Object);
        Assert.NotNull(shrink);
        Assert.True(shrink < 1000, $"full-span sizing should shrink, got {shrink}");

        // Same tick, but the actionable (Collect+Batch) span is only 300ms ⇒ the fleet is light ⇒ GROW.
        var s = NewScaler();
        s.SizeFromActionableSpan = true;
        var grow = DriveToDecision(s, Trade(fullEwmaMs: 800, activeCap: 1000, actionableEwmaMs: 300).Object);
        Assert.NotNull(grow);
        Assert.True(grow > 1000, $"actionable-span sizing should grow, got {grow}");
    }

    [Fact]
    public void CapDecision_alwaysRespectsFloorAndCeiling()
    {
        // Heavy overload at the floor cannot go below MinBotCap(1); heavy underload at the ceiling holds.
        var atFloor = DriveToDecision(NewScaler(), Trade(fullEwmaMs: 5000, activeCap: 1).Object);
        Assert.True(atFloor is null or >= 1);

        var atCeil = DriveToDecision(NewScaler(), Trade(fullEwmaMs: 50, activeCap: 20000, maxCap: 20000).Object);
        Assert.True(atCeil is null or <= 20000);
    }

    [Fact]
    public void RotatorLoadSignal_isMaintained_afterASample()
    {
        var s = NewScaler();
        DriveToDecision(s, Trade(fullEwmaMs: 800, activeCap: 1000).Object);
        // LoadFractionEwma is published for the rotator's opt-in read; it tracks LastLoadFraction.
        Assert.True(s.LoadFractionEwma > 0.0);
        Assert.True(s.LastLoadFraction > 0.0);
    }

    [Fact]
    public void MaxTickMultiple_1_holdsGrowthAtInterval()
    {
        // §R2-1: k=1 recenters the band so the guard binds at work == interval (duty 0.5). With the full tick
        // AT the interval, the guard refuses the increase even though the actionable span says "grow".
        var held = NewScaler();
        held.CorrectDutyCycleDenominator = true;
        held.SizeFromActionableSpan = true;
        held.ApplyMaxTickMultiple(1.0);
        Assert.Equal(0.5, held.TickGuardFraction, 6);
        Assert.Null(DriveToDecision(held, Trade(fullEwmaMs: 1000, activeCap: 100, actionableEwmaMs: 100).Object));

        // Full tick well under the interval ⇒ guard clear ⇒ the cap grows.
        var grows = NewScaler();
        grows.CorrectDutyCycleDenominator = true;
        grows.SizeFromActionableSpan = true;
        grows.ApplyMaxTickMultiple(1.0);
        var grow = DriveToDecision(grows, Trade(fullEwmaMs: 300, activeCap: 100, actionableEwmaMs: 100).Object);
        Assert.NotNull(grow);
        Assert.True(grow > 100, $"expected k=1 to grow when the tick is under the interval, got {grow}");
    }

    [Fact]
    public void MaxTickMultiple_4_growsToFourInterval_thenShrinks()
    {
        // §R2-1: k=4 recenters the band on a 4× tick (guard/target duty 0.8, shrink at duty(5)≈0.833). At
        // ewma 2500 (loadFrac 2500/3500≈0.714) the OLD fixed 0.70 band would shrink; the k=4 band GROWS.
        var grows = NewScaler();
        grows.CorrectDutyCycleDenominator = true;
        grows.ApplyMaxTickMultiple(4.0);
        Assert.Equal(0.8, grows.TickGuardFraction, 6);
        var grow = DriveToDecision(grows, Trade(fullEwmaMs: 2500, activeCap: 100).Object);
        Assert.NotNull(grow);
        Assert.True(grow > 100, $"expected k=4 to keep growing at a 2.5× tick, got {grow}");

        // Past the deadband (ewma 6000 ⇒ loadFrac 6000/7000≈0.857 ≥ high duty(5)≈0.833) ⇒ shrink.
        var shrinks = NewScaler();
        shrinks.CorrectDutyCycleDenominator = true;
        shrinks.ApplyMaxTickMultiple(4.0);
        var shrink = DriveToDecision(shrinks, Trade(fullEwmaMs: 6000, activeCap: 1000).Object);
        Assert.NotNull(shrink);
        Assert.True(shrink < 1000, $"expected k=4 to shrink past a 5× tick, got {shrink}");
    }

    [Fact]
    public void MaxTickMultiple_unset_and_denomOff_isByteIdentical()
    {
        // ApplyMaxTickMultiple(k≤0) is a no-op, so the wiring's "skip when the corrected denominator is off"
        // leaves the band exactly at its defaults ⇒ byte-identical to today's control path.
        var s = NewScaler();
        s.ApplyMaxTickMultiple(0.0);
        Assert.Equal(0.70, s.HighLoadFraction, 6);
        Assert.Equal(0.60, s.TargetLoadFraction, 6);
        Assert.Equal(0.50, s.LowLoadFraction, 6);
        Assert.Equal(1.0, s.TickGuardFraction, 6);

        // And the decision is exactly today's: ewma 800 / interval 1000 = 0.8 ≥ 0.70 ⇒ shrink.
        var shrink = DriveToDecision(s, Trade(fullEwmaMs: 800, activeCap: 1000).Object);
        Assert.NotNull(shrink);
        Assert.True(shrink < 1000, $"expected the unset/off path to shrink like today, got {shrink}");
    }
}

/// <summary>Serial collection so the process-global <see cref="TimeHelper.NowUtc"/> clock can't leak across parallel tests.</summary>
[CollectionDefinition("ClockSerial", DisableParallelization = true)]
public sealed class ClockSerialCollection { }
