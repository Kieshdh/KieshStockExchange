using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §direct-flow chaser: pure-static tests for the deterministic selection (<see
/// cref="AiBotDecisionService.ChaseSelectCore"/>) and the mark-independent sizing (<see
/// cref="AiBotDecisionService.ChaseNotionalCap"/>). No RNG, no I/O — mirrors <see cref="BotMathTests"/>.
/// These pin the two properties the adversarial reviews flagged as load-bearing: selection is independent
/// of watchlist iteration order (incl. cap-saturated ties), and chase size does NOT grow with live price.
/// </summary>
public class ChaserDirectFlowTests
{
    // fraction = 1.0 ⇒ every bot is a chaser, so selection is exercised independent of the cohort hash.
    private const double AllChase = 1.0;
    private const int Salt = 0x5A17;

    private static int IdShock(int sid) => sid; // shockId == stockId for the test (any stable mapping works)

    [Fact]
    public void ChaseSelectCore_picks_max_abs_shock_and_is_order_independent()
    {
        // Two watchlists with the SAME logical set but DIFFERENT iteration order must yield the SAME pick —
        // this is the HashSet-iteration-order determinism guarantee.
        double Shock(int sid) => sid switch { 10 => 0.02, 20 => -0.05, 30 => 0.01, _ => 0.0 };

        // args: candidates, shockOf, shockIdOf, aiUserId, fraction, salt, floor
        var a = AiBotDecisionService.ChaseSelectCore(new[] { 10, 20, 30 }, Shock, IdShock, 7, AllChase, Salt, 0.0);
        var b = AiBotDecisionService.ChaseSelectCore(new[] { 30, 10, 20 }, Shock, IdShock, 7, AllChase, Salt, 0.0);

        Assert.NotNull(a);
        Assert.Equal(20, a!.Value.StockId);        // |−0.05| is the largest
        Assert.Equal(-0.05, a.Value.Shock, 12);    // sign preserved (drives sell)
        Assert.Equal(a, b);                        // identical regardless of input order
    }

    [Fact]
    public void ChaseSelectCore_breaks_cap_saturated_ties_by_lowest_id()
    {
        // Both shocks saturate at the same magnitude (common once accumulators hit ±Cap) — the tie-break must
        // be total and stable (lowest stockId), independent of iteration order.
        double Shock(int sid) => sid is 40 or 25 ? 0.06 : 0.0;

        var a = AiBotDecisionService.ChaseSelectCore(new[] { 40, 25 }, Shock, IdShock, 3, AllChase, Salt, 0.0);
        var b = AiBotDecisionService.ChaseSelectCore(new[] { 25, 40 }, Shock, IdShock, 3, AllChase, Salt, 0.0);

        Assert.NotNull(a);
        Assert.Equal(25, a!.Value.StockId);        // lowest id wins the tie
        Assert.Equal(a, b);
    }

    [Fact]
    public void ChaseSelectCore_returns_null_when_no_live_or_eligible_shock()
    {
        Assert.Null(AiBotDecisionService.ChaseSelectCore(System.Array.Empty<int>(), _ => 0.05, IdShock, 1, AllChase, Salt, 0.0));
        // All shocks at/below the floor ⇒ nothing to chase.
        Assert.Null(AiBotDecisionService.ChaseSelectCore(new[] { 1, 2 }, _ => 0.0, IdShock, 1, AllChase, Salt, 0.0));
        // No bot in the cohort (fraction 0) ⇒ null.
        Assert.Null(AiBotDecisionService.ChaseSelectCore(new[] { 1 }, _ => 0.05, IdShock, 1, 0.0, Salt, 0.0));
    }

    [Fact]
    public void ChaseNotionalCap_is_independent_of_live_price_and_capped()
    {
        // Anti-amplifier invariant: sizing is a function of (shock, seedPortfolio), NOT live price. The caller
        // passes a SEED-price portfolio base, so a 2× live-price move does not appear here at all — the SAME
        // seed base yields the SAME notional. We assert the cap binds and scaling is monotone in seed PV.
        const double cap = 0.06, frac = 0.01, maxFrac = 0.02;

        var small = AiBotDecisionService.ChaseNotionalCap(0.06, cap, frac, maxFrac, seedPortfolio: 10_000m);
        var same  = AiBotDecisionService.ChaseNotionalCap(0.06, cap, frac, maxFrac, seedPortfolio: 10_000m);
        var big   = AiBotDecisionService.ChaseNotionalCap(0.06, cap, frac, maxFrac, seedPortfolio: 20_000m);

        Assert.Equal(small, same);                 // pure
        Assert.True(big > small);                  // scales with the (seed) base, deterministically
        Assert.True(small <= (decimal)maxFrac * 10_000m);   // per-order cap binds
        Assert.True(big   <= (decimal)maxFrac * 20_000m);
        // Zero / non-positive base ⇒ no order.
        Assert.Equal(0m, AiBotDecisionService.ChaseNotionalCap(0.06, cap, frac, maxFrac, 0m));
        // Floor intensity keeps a small shock from sizing to ~0 (persistence across the shock's life).
        var tinyShock = AiBotDecisionService.ChaseNotionalCap(0.0001, cap, frac, maxFrac: 1.0, seedPortfolio: 10_000m);
        Assert.True(tinyShock > 0m);
    }
}
