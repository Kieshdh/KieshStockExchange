using System;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §reaction-persistence split — pure-static + context-helper tests for the two-clock reaction/persistence
/// lever. Mirrors the SmoothedPriceEwmaTests (AR(1) keep), BotMathTests (dispersion + odd-symmetry) and
/// RefillThrottleDeterminismTests (draw-free-when-off, sentinel-delegate) precedents — no RNG, no I/O.
/// </summary>
public class ReactionPersistenceTests
{
    private static AiBotContext NewCtx()
        => new(new Mock<IAccountsCache>(MockBehavior.Loose).Object, personalSentiment: false);

    private const long Sec = TimeSpan.TicksPerSecond;

    // ---- AR(1) / half-life keep (BotMath.HalfLifeKeep) ----

    [Fact]
    public void HalfLifeKeep_matches_the_0p5_pow_shape()
    {
        Assert.Equal(0.5, BotMath.HalfLifeKeep(60.0, 60.0), 12);   // one half-life ⇒ half
        Assert.Equal(0.25, BotMath.HalfLifeKeep(120.0, 60.0), 12); // two ⇒ quarter
        Assert.Equal(1.0, BotMath.HalfLifeKeep(5.0, 0.0));         // halfLife 0 ⇒ off/no decay
        Assert.Equal(1.0, BotMath.HalfLifeKeep(0.0, 60.0));        // dt 0 ⇒ keep old fully
        Assert.Equal(1.0, BotMath.HalfLifeKeep(-3.0, 60.0));       // clock skew ⇒ no decay
        // keep decreases monotonically with dt.
        Assert.True(BotMath.HalfLifeKeep(10.0, 60.0) > BotMath.HalfLifeKeep(30.0, 60.0));
        Assert.True(BotMath.HalfLifeKeep(30.0, 60.0) > BotMath.HalfLifeKeep(90.0, 60.0));
    }

    // ---- UpdatePressure (the per-bot AR(1) state machine) ----

    [Fact]
    public void UpdatePressure_first_sight_seeds_zero_and_populates_dicts_once()
    {
        var ctx = NewCtx();
        Assert.Equal(0m, ctx.UpdatePressure(7, fresh: 1m, nowTicks: 1000L, halfLifeSec: 300.0));
        Assert.True(ctx.Pressures.ContainsKey(7));
        Assert.True(ctx.PressureUpdatedTicks.ContainsKey(7));
        Assert.Equal(0m, ctx.Pressures[7]);
        Assert.Single(ctx.Pressures);
    }

    [Fact]
    public void UpdatePressure_zero_dt_and_zero_halflife_are_noops()
    {
        var ctx = NewCtx();
        long t0 = 5 * Sec;
        ctx.UpdatePressure(7, 1m, t0, 300.0);                 // seed 0
        // dt = 0 (same tick) ⇒ keep 1 ⇒ unchanged.
        Assert.Equal(0m, ctx.UpdatePressure(7, 1m, t0, 300.0));
        // advance one half-life so pressure is non-zero, then a halfLife<=0 call must be a no-op.
        var p = ctx.UpdatePressure(7, 1m, t0 + 300 * Sec, 300.0);
        Assert.True(p > 0m);
        Assert.Equal(p, ctx.UpdatePressure(7, 1m, t0 + 600 * Sec, 0.0));
    }

    [Fact]
    public void UpdatePressure_blends_by_half_and_then_decays_toward_zero()
    {
        var ctx = NewCtx();
        long t = 0L;
        ctx.UpdatePressure(3, 1m, t, 300.0);                       // seed 0
        var p1 = ctx.UpdatePressure(3, 1m, t += 300 * Sec, 300.0); // keep .5 ⇒ .5·0 + .5·1
        Assert.Equal(0.5m, p1, 6);
        var p2 = ctx.UpdatePressure(3, 1m, t += 300 * Sec, 300.0); // .5·.5 + .5·1
        Assert.Equal(0.75m, p2, 6);
        var p3 = ctx.UpdatePressure(3, 0m, t += 300 * Sec, 300.0); // fresh 0 ⇒ decays: .5·.75
        Assert.Equal(0.375m, p3, 6);
        Assert.True(p3 < p2);
    }

    // ---- Per-bot persistence half-life dispersion (AiBotDecisionService.PersistHalfLife) ----

    [Fact]
    public void PersistHalfLife_is_in_range_deterministic_and_spread()
    {
        const int n = 20_000;
        double min = 300.0, max = 1200.0;
        int lo = 0, mid = 0, hi = 0;
        double third = (max - min) / 3.0;
        for (int id = 1; id <= n; id++)
        {
            double hl = AiBotDecisionService.PersistHalfLife(id, min, max);
            Assert.InRange(hl, min, max);
            Assert.Equal(hl, AiBotDecisionService.PersistHalfLife(id, min, max)); // pure
            if (hl < min + third) lo++;
            else if (hl < min + 2 * third) mid++;
            else hi++;
        }
        // Population covers the whole band (no clumping into one tertile).
        Assert.True(lo > n / 6 && mid > n / 6 && hi > n / 6, $"tertiles=({lo},{mid},{hi})");
        // Negative id + high-bit salt path is (uint)-safe and deterministic.
        Assert.Equal(
            AiBotDecisionService.PersistHalfLife(-12345, min, max),
            AiBotDecisionService.PersistHalfLife(-12345, min, max));
    }

    // ---- Direction tilt (AiBotDecisionService.PressureTilt) ----

    [Fact]
    public void PressureTilt_is_odd_symmetric_and_saturates_to_the_leak_bounds()
    {
        Assert.Equal(0.5m, AiBotDecisionService.PressureTilt(0.5m, 0m, 0.10m));       // no pressure ⇒ unchanged
        Assert.Equal(0.90m, AiBotDecisionService.PressureTilt(0.5m, 1m, 0.10m), 6);   // full buy ⇒ 1−leak
        Assert.Equal(0.10m, AiBotDecisionService.PressureTilt(0.5m, -1m, 0.10m), 6);  // full sell ⇒ leak
        // odd-symmetric about 0.5.
        var up   = AiBotDecisionService.PressureTilt(0.5m, 0.4m, 0.10m) - 0.5m;
        var down = 0.5m - AiBotDecisionService.PressureTilt(0.5m, -0.4m, 0.10m);
        Assert.Equal(up, down, 6);
    }

    // ---- Taker override mapping + draw discipline (ctx.PressureTakerOverride) ----

    [Fact]
    public void PressureTakerOverride_crosses_the_spread_in_the_pressure_direction()
    {
        var ctx = NewCtx();
        // above threshold, draw 0 < p ⇒ override to a market taker on the pressure side.
        Assert.True(ctx.PressureTakerOverride(true, 0.5m, 0.15m, 1.0m, () => 0m, out var mkt, out var buy));
        Assert.True(mkt);
        Assert.True(buy);                                   // pressure > 0 ⇒ buy
        Assert.True(ctx.PressureTakerOverride(true, -0.5m, 0.15m, 1.0m, () => 0m, out _, out var sBuy));
        Assert.False(sBuy);                                 // pressure < 0 ⇒ sell
    }

    [Fact]
    public void PressureTakerOverride_high_draw_does_not_override()
    {
        var ctx = NewCtx();
        // |pressure|>=threshold but draw >= p ⇒ no override (the probabilistic gate actually gates).
        Assert.False(ctx.PressureTakerOverride(true, 0.5m, 0.15m, 1.0m, () => 0.9m, out _, out _));
    }

    [Fact]
    public void PressureTakerOverride_is_drawfree_when_off_or_below_threshold()
    {
        var ctx = NewCtx();
        bool drew = false;
        Func<decimal> draw = () => { drew = true; return 0m; };
        // lever off ⇒ no override, seeded draw NOT taken (byte-identical RNG stream).
        Assert.False(ctx.PressureTakerOverride(false, 0.9m, 0.15m, 1.0m, draw, out _, out _));
        Assert.False(drew);
        // below threshold ⇒ no override, still draw-free.
        Assert.False(ctx.PressureTakerOverride(true, 0.10m, 0.15m, 1.0m, draw, out _, out _));
        Assert.False(drew);
    }

    // ---- Runaway governor (AiBotDecisionService.ReactionTakerEffectiveGain) ----

    [Fact]
    public void ReactionTakerEffectiveGain_tapers_with_value_gap()
    {
        // Huge scale (default) ⇒ ~no taper.
        Assert.Equal(1.0m, AiBotDecisionService.ReactionTakerEffectiveGain(1.0m, 0.20m, 1_000_000_000m), 6);
        // Monotone decreasing, →0 at the scale.
        Assert.Equal(1.0m, AiBotDecisionService.ReactionTakerEffectiveGain(1.0m, 0.0m, 1.0m), 6);
        Assert.Equal(0.5m, AiBotDecisionService.ReactionTakerEffectiveGain(1.0m, 0.5m, 1.0m), 6);
        Assert.Equal(0.0m, AiBotDecisionService.ReactionTakerEffectiveGain(1.0m, 1.0m, 1.0m), 6);
        Assert.Equal(0.0m, AiBotDecisionService.ReactionTakerEffectiveGain(1.0m, 2.0m, 1.0m), 6); // clamped, not negative
        // govScale <= 0 ⇒ ungoverned (returns takerGain).
        Assert.Equal(1.0m, AiBotDecisionService.ReactionTakerEffectiveGain(1.0m, 5.0m, 0.0m), 6);
    }
}
