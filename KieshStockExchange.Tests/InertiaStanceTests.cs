using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §A1 inertia: a bot's directional STANCE must persist across ticks until it expires, and the
/// roll-or-hold helper must consume EXACTLY two seeded draws on a (re)roll and ZERO while a stance holds
/// (the determinism contract that makes inert-first real — no stray RNG on the disabled/held path).
/// </summary>
public class InertiaStanceTests
{
    private const int Id = 5;

    private static AiBotContext NewCtx()
    {
        var accounts = new Mock<IAccountsCache>(MockBehavior.Loose).Object;
        var ctx = new AiBotContext(accounts, personalSentiment: false);
        ctx.AiUsersByAiUserId[Id] = new AIUser { AiUserId = Id, UserId = Id, Seed = 123 };
        return ctx;
    }

    [Fact]
    public void Stance_holds_its_direction_across_T_then_flips_after_expiry()
    {
        var ctx = NewCtx();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // buyProb = 1 → dir +1 (Decimal01 is always < 1); fixed 100s window (min==max).
        var dir0 = ctx.RollOrHoldStance(Id, buyProb: 1.0m, now: t0, minSec: 100, maxSec: 100);
        Assert.Equal((sbyte)1, dir0);
        Assert.True(ctx.Stances.TryGetValue(Id, out var st));
        Assert.Equal((sbyte)1, st.dir);
        Assert.Equal(t0.AddSeconds(100), st.until);

        // Mid-window the stance HOLDS +1 even though buyProb now says "always sell".
        var dirMid = ctx.RollOrHoldStance(Id, buyProb: 0.0m, now: t0.AddSeconds(50), minSec: 100, maxSec: 100);
        Assert.Equal((sbyte)1, dirMid);

        // After expiry it rolls fresh: buyProb 0 → dir -1.
        var dirAfter = ctx.RollOrHoldStance(Id, buyProb: 0.0m, now: t0.AddSeconds(150), minSec: 100, maxSec: 100);
        Assert.Equal((sbyte)-1, dirAfter);
    }

    [Fact]
    public void Roll_consumes_exactly_two_draws_and_a_hold_consumes_none()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Baseline: the first four raw draws from an identical RNG (same seed/id/date).
        var b = NewCtx();
        decimal r0 = b.Decimal01(Id), r1 = b.Decimal01(Id), r2 = b.Decimal01(Id), r3 = b.Decimal01(Id);

        var t = NewCtx();
        // A fresh roll consumes draw0 (dir) + draw1 (duration); the NEXT raw draw must be draw2.
        t.RollOrHoldStance(Id, buyProb: 0.5m, now: t0, minSec: 30, maxSec: 600);
        Assert.Equal(r2, t.Decimal01(Id));
        // The stance is still active (duration ≥ 30s) → a hold draws nothing; next raw draw is draw3.
        t.RollOrHoldStance(Id, buyProb: 0.5m, now: t0.AddSeconds(1), minSec: 30, maxSec: 600);
        Assert.Equal(r3, t.Decimal01(Id));

        // Guard against an accidental dependency on the unused baseline head.
        Assert.NotEqual(r0, r1);
    }

    // §sentiment-modulated inertia: pins the pure helper that shrinks the max hold toward minSec as the
    // |shared sentiment| magnitude (0..1) rises. mag 0 ⇒ maxSec (byte-identical to today); mag 1 ⇒ minSec.
    [Fact]
    public void SentimentMaxSec_zero_magnitude_returns_maxSec()
        => Assert.Equal(600.0, AiBotDecisionService.SentimentModulatedMaxSec(30.0, 600.0, 0.0));

    [Fact]
    public void SentimentMaxSec_full_magnitude_returns_minSec()
        => Assert.Equal(30.0, AiBotDecisionService.SentimentModulatedMaxSec(30.0, 600.0, 1.0));

    [Fact]
    public void SentimentMaxSec_half_magnitude_returns_midpoint()
        => Assert.Equal(315.0, AiBotDecisionService.SentimentModulatedMaxSec(30.0, 600.0, 0.5));

    [Fact]
    public void SentimentMaxSec_clamps_negative_to_maxSec()
        => Assert.Equal(600.0, AiBotDecisionService.SentimentModulatedMaxSec(30.0, 600.0, -0.40));

    [Fact]
    public void SentimentMaxSec_clamps_above_one_to_minSec()
        => Assert.Equal(30.0, AiBotDecisionService.SentimentModulatedMaxSec(30.0, 600.0, 1.50));
}
