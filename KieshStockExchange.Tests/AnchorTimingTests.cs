using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// R5 anchor-timing fix. Option C (AnchorDeadband) zeroes the anchor pull within ±band and passes only the
/// excess; Option B (LaggedAnchorTilt) is a per-bot EWMA whose alpha is staggered by Lateness so the cohort's
/// correction spreads across minutes (L=0 fast, L=1 slow) — seeded at the target so there's no startup snap.
/// </summary>
public class AnchorTimingTests
{
    private const int Id = 7;
    private const CurrencyType Ccy = CurrencyType.USD;

    private static AiBotContext NewCtx()
    {
        var accounts = new Mock<IAccountsCache>(MockBehavior.Loose).Object;
        return new AiBotContext(accounts, personalSentiment: false);
    }

    // ---- Option C: dead-band ----

    [Fact]
    public void Deadband_off_is_passthrough()
    {
        Assert.Equal(0.12m, AiBotDecisionService.AnchorDeadband(0.12m, 0m));
        Assert.Equal(-0.12m, AiBotDecisionService.AnchorDeadband(-0.12m, 0m));
    }

    [Fact]
    public void Deadband_zeroes_within_band()
    {
        Assert.Equal(0m, AiBotDecisionService.AnchorDeadband(0.02m, 0.03m));
        Assert.Equal(0m, AiBotDecisionService.AnchorDeadband(-0.03m, 0.03m)); // at the edge ⇒ still zero
    }

    [Fact]
    public void Deadband_passes_only_the_signed_excess_beyond_the_band()
    {
        Assert.Equal(0.02m, AiBotDecisionService.AnchorDeadband(0.05m, 0.03m));   // +5% gap, 3% band ⇒ +2%
        Assert.Equal(-0.02m, AiBotDecisionService.AnchorDeadband(-0.05m, 0.03m)); // sign preserved
    }

    // ---- Option B: Lateness-staggered EWMA ----

    [Fact]
    public void LaggedAnchorTilt_seeds_at_target_on_first_sight()
    {
        var ctx = NewCtx();
        // First call returns the target exactly regardless of alpha (no 0 → target transient).
        var first = ctx.LaggedAnchorTilt(Id, Ccy, target: 0.4m, lateness: 1m, minAlpha: 0.05m, maxAlpha: 0.30m);
        Assert.Equal(0.4m, first);
    }

    [Fact]
    public void LaggedAnchorTilt_fast_bot_tracks_quickly()
    {
        var ctx = NewCtx();
        // L=0 ⇒ alpha = maxAlpha. Seed at 0, then a new target moves a maxAlpha-fraction of the gap.
        ctx.LaggedAnchorTilt(Id, Ccy, target: 0m, lateness: 0m, minAlpha: 0.05m, maxAlpha: 0.30m);
        var step = ctx.LaggedAnchorTilt(Id, Ccy, target: 1m, lateness: 0m, minAlpha: 0.05m, maxAlpha: 0.30m);
        Assert.Equal(0.30m, step); // 0 + 0.30*(1-0)
    }

    [Fact]
    public void LaggedAnchorTilt_slow_bot_lags_behind_fast_bot()
    {
        var ctx = NewCtx();
        const int Fast = 1, Slow = 2;
        // Both seeded at 0, then chase target 1. The high-L bot must move strictly less per step.
        ctx.LaggedAnchorTilt(Fast, Ccy, 0m, lateness: 0m, minAlpha: 0.05m, maxAlpha: 0.30m);
        ctx.LaggedAnchorTilt(Slow, Ccy, 0m, lateness: 1m, minAlpha: 0.05m, maxAlpha: 0.30m);

        var fast = ctx.LaggedAnchorTilt(Fast, Ccy, 1m, lateness: 0m, minAlpha: 0.05m, maxAlpha: 0.30m);
        var slow = ctx.LaggedAnchorTilt(Slow, Ccy, 1m, lateness: 1m, minAlpha: 0.05m, maxAlpha: 0.30m);

        Assert.Equal(0.30m, fast);
        Assert.Equal(0.05m, slow);
        Assert.True(slow < fast, "high-Lateness bot must lag the fast bot");
    }

    [Fact]
    public void LaggedAnchorTilt_converges_to_a_held_target()
    {
        var ctx = NewCtx();
        ctx.LaggedAnchorTilt(Id, Ccy, 0m, lateness: 1m, minAlpha: 0.05m, maxAlpha: 0.30m); // seed 0
        decimal last = 0m;
        for (int i = 0; i < 500; i++)
            last = ctx.LaggedAnchorTilt(Id, Ccy, 1m, lateness: 1m, minAlpha: 0.05m, maxAlpha: 0.30m);
        Assert.True(last > 0.99m, $"slow EWMA should still converge to the held target, got {last}");
    }

    [Fact]
    public void LaggedAnchorTilt_state_is_per_user_and_currency()
    {
        var ctx = NewCtx();
        // Seeding user/ccy A must not leak into a different key.
        ctx.LaggedAnchorTilt(Id, Ccy, target: 0.9m, lateness: 1m, minAlpha: 0.05m, maxAlpha: 0.30m);
        var otherUser = ctx.LaggedAnchorTilt(Id + 1, Ccy, target: 0.1m, lateness: 1m, minAlpha: 0.05m, maxAlpha: 0.30m);
        Assert.Equal(0.1m, otherUser); // fresh key seeds at its own target
    }
}
