using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.PortfolioServices.Interfaces;
using Moq;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §impact-decouple determinism + byte-identical-off tests for the two flag-gated mechanisms that break the
/// 1-min self-impact reaction loop (the ret_acf_lag1 ≈ −0.43 ceiling):
///   • A (ImpactDecoupleReference): <see cref="AiBotContext.ReactionRefOr"/> drives the reaction off a &gt;1-min
///     reference price. OFF ⇒ returns the legacy fallback ⇒ byte-identical.
///   • B (ImpactDecoupleHold): <see cref="AiBotContext.HeldDirectional"/> holds the directional stance on a
///     per-bot &gt;1-min refractory so a bot cannot fade its own move within the minute it caused.
/// Both helpers are pure (no RNG, no clock read — the tick clock is passed in), so pinning them here covers the
/// behavioural surface without standing up the full bot loop. Mirrors BotStaggeringDeterminismTests.
/// </summary>
public class ImpactDecoupleDeterminismTests
{
    private static AiBotContext NewContext(bool reactionRef) =>
        new(Mock.Of<IAccountsCache>(), reactionRef: reactionRef);

    private static readonly long TicksPerSec = System.TimeSpan.TicksPerSecond;

    // ---- Mechanism A: reaction reference ------------------------------------------------------------

    [Fact]
    public void ReactionRefOr_off_returns_fallback_even_when_reference_seeded()
    {
        var ctx = NewContext(reactionRef: false);
        var key = (5, CurrencyType.USD);
        ctx.ReactionRefPrices[key] = 100m;                 // seeded, but flag off ⇒ must be ignored
        Assert.Equal(42m, ctx.ReactionRefOr(key, 42m));    // byte-identical: returns the legacy fallback
    }

    [Fact]
    public void ReactionRefOr_on_uses_reference_when_seeded_else_fallback()
    {
        var ctx = NewContext(reactionRef: true);
        var key = (5, CurrencyType.USD);
        Assert.Equal(42m, ctx.ReactionRefOr(key, 42m));    // unseeded ⇒ fallback
        ctx.ReactionRefPrices[key] = 100m;
        Assert.Equal(100m, ctx.ReactionRefOr(key, 42m));   // seeded ⇒ the >1-min reference
        ctx.ReactionRefPrices[key] = 0m;                   // non-positive ⇒ fallback (guards a stuck/zero ref)
        Assert.Equal(42m, ctx.ReactionRefOr(key, 42m));
    }

    [Fact]
    public void ComputeWatchlistMomentum_off_is_legacy_on_uses_reference()
    {
        var user = new AIUser { HomeCurrencyType = CurrencyType.USD };
        user.AddToWatchlist(11);
        var key = (11, CurrencyType.USD);

        var off = NewContext(reactionRef: false);
        off.SmoothedPrices[key] = 110m; off.PreviousPrices[key] = 100m; off.ReactionRefPrices[key] = 50m;
        Assert.Equal((110m - 100m) / 100m, off.ComputeWatchlistMomentum(user, CurrencyType.USD)); // ignores ref

        var on = NewContext(reactionRef: true);
        on.SmoothedPrices[key] = 110m; on.PreviousPrices[key] = 100m; on.ReactionRefPrices[key] = 50m;
        Assert.Equal((110m - 50m) / 50m, on.ComputeWatchlistMomentum(user, CurrencyType.USD));    // vs reference
    }

    [Fact]
    public void TimeEwmaKeep_matches_half_life_formula()
    {
        // The reference EWMA reuses this pure helper; the contract: dt<=0 or HL<=0 ⇒ keep 1 (no move),
        // one half-life ⇒ keep 0.5. (dt=0 path is the cold-load/first-quote seed = ref stays at LastPrice.)
        Assert.Equal(1.0, AiTradeService.TimeEwmaKeep(0.0, 240.0));
        Assert.Equal(1.0, AiTradeService.TimeEwmaKeep(5.0, 0.0));
        Assert.Equal(0.5, AiTradeService.TimeEwmaKeep(240.0, 240.0), 12);
    }

    // ---- Mechanism B: per-bot refractory ------------------------------------------------------------

    [Fact]
    public void HeldDirectional_holds_within_window_then_adopts_after()
    {
        var ctx = NewContext(reactionRef: false);
        const int uid = 7;
        var ccy = CurrencyType.USD;
        long win = 90L * TicksPerSec;
        long t0  = 1_000_000L * TicksPerSec;

        Assert.Equal(0.4m, ctx.HeldDirectional(uid, ccy, 0.4m, t0, win));               // first ⇒ seed + return
        Assert.Equal(0.4m, ctx.HeldDirectional(uid, ccy, -0.4m, t0 + win / 2, win));    // reversal NOT adopted
        Assert.Equal(0.4m, ctx.HeldDirectional(uid, ccy, -0.4m, t0 + win - 1, win));    // boundary-adjacent: held
        Assert.Equal(-0.4m, ctx.HeldDirectional(uid, ccy, -0.4m, t0 + win, win));       // window elapsed ⇒ adopt
        Assert.Equal(-0.4m, ctx.HeldDirectional(uid, ccy, 0.9m, t0 + win + 5, win));    // held again post-reset
    }

    [Fact]
    public void HeldDirectional_is_noop_when_window_nonpositive()
    {
        var ctx = NewContext(reactionRef: false);
        Assert.Equal(0.3m, ctx.HeldDirectional(1, CurrencyType.USD, 0.3m, 123L, 0L));
        Assert.Equal(-0.7m, ctx.HeldDirectional(1, CurrencyType.USD, -0.7m, 999L, -5L));
        Assert.Empty(ctx.ReactionHold);   // a no-op leaves no per-bot state
    }

    [Fact]
    public void HeldDirectional_is_pure_for_repeated_identical_inputs()
    {
        var ctx = NewContext(reactionRef: false);
        long win = 90L * TicksPerSec;
        ctx.HeldDirectional(2, CurrencyType.USD, 0.6m, 0L, win);            // seed
        decimal a = ctx.HeldDirectional(2, CurrencyType.USD, -1m, 10L, win);
        decimal b = ctx.HeldDirectional(2, CurrencyType.USD, -1m, 10L, win);
        Assert.Equal(a, b);                                                 // reproducible (no hidden state/RNG)
        Assert.Equal(0.6m, a);
    }

    // ---- Cross-cutting invariants -------------------------------------------------------------------

    [Fact]
    public void Reaction_state_survives_per_tick_clear_but_is_wiped_by_ClearAll()
    {
        var ctx = NewContext(reactionRef: true);
        var key = (3, CurrencyType.USD);
        ctx.ReactionRefPrices[key] = 99m;
        ctx.ReactionHold[(3, CurrencyType.USD)] = (0.5m, 123L);

        ctx.ClearTickCaches();                       // per-tick clear must NOT touch persistent reaction state
        Assert.Equal(99m, ctx.ReactionRefPrices[key]);
        Assert.Single(ctx.ReactionHold);

        ctx.ClearAll();                              // full reset clears everything
        Assert.True(ctx.ReactionRefPrices.IsEmpty);
        Assert.Empty(ctx.ReactionHold);
    }

    [Fact]
    public void Reaction_helpers_draw_no_rng()
    {
        // Neither mechanism may touch the per-bot RNG. GetRandom lazily instantiates an entry in AiUserRngs on
        // first use, so an empty AiUserRngs after exercising both helpers proves zero RNG was drawn/seeded.
        var ctx = NewContext(reactionRef: true);
        var key = (4, CurrencyType.USD);
        ctx.SmoothedPrices[key] = 100m; ctx.PreviousPrices[key] = 99m; ctx.ReactionRefPrices[key] = 80m;
        long win = 90L * TicksPerSec;
        for (int i = 0; i < 100; i++)
        {
            ctx.ReactionRefOr(key, 99m);
            ctx.HeldDirectional(4, CurrencyType.USD, 0.01m * i, i * TicksPerSec, win);
        }
        Assert.Empty(ctx.AiUserRngs);
    }

    [Fact]
    public void ImpactHoldProbe_counts_only_when_enabled()
    {
        ImpactHoldProbe.Configure(false);
        ImpactHoldProbe.Drain();                          // reset
        ImpactHoldProbe.Record(held: true);
        ImpactHoldProbe.Record(held: false);
        var (h0, r0, _) = ImpactHoldProbe.Drain();
        Assert.Equal(0L, h0);
        Assert.Equal(0L, r0);                             // disabled ⇒ nothing counted (zero hot-path cost)

        ImpactHoldProbe.Configure(true);
        ImpactHoldProbe.Drain();
        ImpactHoldProbe.Record(held: true);
        ImpactHoldProbe.Record(held: true);
        ImpactHoldProbe.Record(held: false);
        var (h, r, frac) = ImpactHoldProbe.Drain();
        Assert.Equal(2L, h);
        Assert.Equal(1L, r);
        Assert.Equal(2.0 / 3.0, frac, 9);
        ImpactHoldProbe.Configure(false);                 // restore the default-off state
    }
}
