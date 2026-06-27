using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.BackgroundServices.Interfaces;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §refill-throttle determinism + control-loop tests. The gate is a BOUNDED mover-response lever (NOT the
/// open-loop "remove the wall" design that over-corrected as BuyStopFraction). These pin: the resisting-side
/// selection, the pure/symmetric offset-widen, the Schmitt-trigger arm/disarm + re-arm cooldown, the per-event
/// move-budget force-disarm (anti-runaway), and that the context call sites are byte-identical and DRAW-FREE
/// when the lever is off. Mirrors CoMovementDeterminismTests / AdaptiveAnchorDeterminismTests (pure unit tests).
/// </summary>
public class RefillThrottleDeterminismTests
{
    private static RefillThrottleGate Gate(decimal arm = 0.02m, decimal disarm = 0.005m,
        decimal mult = 0.5m, decimal skip = 0m, decimal budget = 0m, long cooldown = 0)
        => new(new RefillThrottleGate.Settings
        {
            Enabled = true,
            Source = RefillThrottleGate.SignalSource.RealizedReturnFast,
            ThresholdArm = arm, ThresholdDisarm = disarm,
            MaxEventMovePct = budget, RearmCooldownTicks = cooldown,
            OffsetWidenMult = mult, SkipRepostProb = skip,
        });

    [Fact]
    public void ResistsMove_table()
    {
        // up-mover (+1) ⇒ SELL/ask resists; down-mover (-1) ⇒ BUY/bid resists; flat ⇒ neither.
        Assert.True(RefillThrottleGate.ResistsMove(isBuy: false, sign: 1));
        Assert.False(RefillThrottleGate.ResistsMove(isBuy: true, sign: 1));
        Assert.True(RefillThrottleGate.ResistsMove(isBuy: true, sign: -1));
        Assert.False(RefillThrottleGate.ResistsMove(isBuy: false, sign: -1));
        Assert.False(RefillThrottleGate.ResistsMove(isBuy: true, sign: 0));
        Assert.False(RefillThrottleGate.ResistsMove(isBuy: false, sign: 0));
    }

    [Fact]
    public void Widen_is_noop_on_nonresisting_side_and_when_mult_zero()
    {
        var g = Gate(mult: 0.5m);
        Assert.Equal(1m, g.WidenFactor(isBuy: true, sign: 1, intensity: 1m));   // buy does not resist an up-mover
        Assert.Equal(1.5m, g.WidenFactor(isBuy: false, sign: 1, intensity: 1m)); // sell resists ⇒ widened

        var off = Gate(mult: 0m);
        Assert.Equal(1m, off.WidenFactor(isBuy: false, sign: 1, intensity: 1m)); // Mult 0 ⇒ byte-identical
    }

    [Fact]
    public void Widen_magnitude_is_symmetric_across_sides()
    {
        // The widen factor must be the SAME function of |signal| for an up-mover's sell and a down-mover's buy,
        // else the lever injects net drift (the BuyStopFraction symptom).
        var g = Gate(mult: 0.7m);
        Assert.Equal(g.WidenFactor(isBuy: false, sign: 1, intensity: 1m),
                     g.WidenFactor(isBuy: true, sign: -1, intensity: 1m));
    }

    [Fact]
    public void Arms_only_above_arm_threshold()
    {
        var g = Gate(arm: 0.02m);
        Assert.Equal((sbyte)0, g.Step(1, signal: 0.01m, price: 100m, tick: 1).sign);  // below arm ⇒ idle
        Assert.Equal((sbyte)1, g.Step(1, signal: 0.03m, price: 100m, tick: 2).sign);  // above arm ⇒ armed up
    }

    [Fact]
    public void Hysteresis_holds_between_thresholds_then_self_extinguishes()
    {
        var g = Gate(arm: 0.02m, disarm: 0.005m);
        Assert.Equal((sbyte)1, g.Step(1, 0.03m, 100m, 1).sign);                 // arm
        Assert.Equal((sbyte)1, g.Step(1, 0.01m, 100m, 2).sign);                 // between thresholds ⇒ hold
        Assert.Equal((sbyte)0, g.Step(1, 0.0009m, 100m, 3).sign);              // below disarm ⇒ releases (self-extinguish)
    }

    [Fact]
    public void Flip_disarms()
    {
        var g = Gate(arm: 0.02m, disarm: 0.005m);
        Assert.Equal((sbyte)1, g.Step(1, 0.03m, 100m, 1).sign);   // armed up
        Assert.Equal((sbyte)0, g.Step(1, -0.03m, 100m, 2).sign);  // opposite sign ⇒ disarm (no instant flip-latch)
    }

    [Fact]
    public void Rearm_cooldown_blocks_immediate_rearm()
    {
        var g = Gate(arm: 0.02m, disarm: 0.005m, cooldown: 5);
        Assert.Equal((sbyte)1, g.Step(1, 0.03m, 100m, 1).sign);    // arm
        Assert.Equal((sbyte)0, g.Step(1, 0.0009m, 100m, 2).sign);  // disarm at tick 2
        Assert.Equal((sbyte)0, g.Step(1, 0.03m, 100m, 3).sign);    // tick 3: still cooling (3-2 < 5) ⇒ no rearm
        Assert.Equal((sbyte)1, g.Step(1, 0.03m, 100m, 8).sign);    // tick 8: cooldown elapsed ⇒ rearm
    }

    [Fact]
    public void Move_budget_force_disarms_even_with_hot_signal()
    {
        // Anti-runaway: once cumulative displacement since arming reaches MaxEventMovePct, the gate force-disarms
        // so the wall reforms and the move arrests — EVEN though the signal is still above the arm threshold.
        var g = Gate(arm: 0.02m, disarm: 0.001m, budget: 0.05m);
        Assert.Equal((sbyte)1, g.Step(1, 0.03m, price: 100m, tick: 1).sign);   // arm at 100
        Assert.Equal((sbyte)0, g.Step(1, 0.03m, price: 106m, tick: 2).sign);   // +6% ≥ 5% budget ⇒ force-disarm
    }

    [Fact]
    public void Step_is_deterministic_for_same_inputs()
    {
        var a = Gate(); var b = Gate();
        for (long t = 1; t <= 50; t++)
        {
            decimal sig = (t % 7 == 0) ? 0.03m : 0.0m;
            Assert.Equal(a.Step(1, sig, 100m, t), b.Step(1, sig, 100m, t));
        }
    }

    [Fact]
    public void Context_helpers_are_noop_and_drawfree_when_gate_off()
    {
        // RefillGate left null ⇒ lever off ⇒ widen factor 1.0, never skips, and the seeded draw is NOT taken
        // (byte-identical RNG stream — the core default-off invariant).
        var ctx = new AiBotContext(new Mock<IAccountsCache>(MockBehavior.Loose).Object, personalSentiment: false);
        Assert.Equal(1m, ctx.RefillWidenFactor(1, CurrencyType.USD, isBuy: true));

        bool drew = false;
        bool skip = ctx.RefillShouldSkip(1, CurrencyType.USD, isBuy: false, () => { drew = true; return 0m; });
        Assert.False(skip);
        Assert.False(drew);
    }
}
