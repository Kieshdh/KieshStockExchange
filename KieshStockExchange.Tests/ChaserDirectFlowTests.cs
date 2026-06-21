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

    // ───────────────────────── §chaser-v2 ratio-fix co-dials ─────────────────────────
    // ChaseFloorIntensity (0.25) is the floorFrac the production call site passes; mirror it here.
    private const double FloorFrac = 0.25;

    [Fact]
    public void ChaseSymmetricSellQty_off_is_passthrough()
    {
        // symFrac <= 0 ⇒ feature off ⇒ the desired qty is returned untouched, regardless of room/cash.
        Assert.Equal(100, AiBotDecisionService.ChaseSymmetricSellQty(
            desiredSellQty: 100, estimatePrice: 10m, buyRoomValue: 0m, spendableBuyValue: 0m,
            capValue: 1_000m, symFrac: 0.0, floorFrac: FloorFrac));
    }

    [Fact]
    public void ChaseSymmetricSellQty_only_ever_reduces_and_caps_to_buy_ceiling()
    {
        // symFrac = 1 ⇒ sell capped to min(room, spendable)/price. room=300, spendable=500 ⇒ ceil=300 ⇒ 30 shares.
        var capped = AiBotDecisionService.ChaseSymmetricSellQty(
            desiredSellQty: 100, estimatePrice: 10m, buyRoomValue: 300m, spendableBuyValue: 500m,
            capValue: 1_000m, symFrac: 1.0, floorFrac: 0.0);
        Assert.Equal(30, capped);
        Assert.InRange(capped, 0, 100);               // never increases the desired qty

        // A bot whose buy-ceiling already exceeds the desired notional is barely touched (returns desired).
        var loose = AiBotDecisionService.ChaseSymmetricSellQty(
            desiredSellQty: 10, estimatePrice: 10m, buyRoomValue: 10_000m, spendableBuyValue: 10_000m,
            capValue: 50_000m, symFrac: 1.0, floorFrac: 0.0);
        Assert.Equal(10, loose);
    }

    [Fact]
    public void ChaseSymmetricSellQty_net_long_throttled_vs_flat_bot()
    {
        // A net-LONG bot sits near its cap ⇒ small room ⇒ heavily throttled. A flat bot has full room ⇒ free.
        // Same shares-held desire (100 @ $10) and same capValue; only room differs.
        var atCapLong = AiBotDecisionService.ChaseSymmetricSellQty(
            100, 10m, buyRoomValue: 50m, spendableBuyValue: 10_000m, capValue: 1_000m, symFrac: 1.0, floorFrac: 0.0);
        var flat = AiBotDecisionService.ChaseSymmetricSellQty(
            100, 10m, buyRoomValue: 1_000m, spendableBuyValue: 10_000m, capValue: 1_000m, symFrac: 1.0, floorFrac: 0.0);
        Assert.True(atCapLong < flat);                // the over-long bot is the one throttled
        Assert.Equal(5, atCapLong);                   // 50/10
    }

    [Fact]
    public void ChaseSymmetricSellQty_floor_keeps_at_cap_long_from_freezing()
    {
        // roomValue == 0 (exactly at position cap). Without a floor the sell would clamp to 0 (frozen long).
        // floorFrac · capValue = 0.25 · 1000 = 250 ⇒ 25 shares can still be shed into the shock.
        var floored = AiBotDecisionService.ChaseSymmetricSellQty(
            desiredSellQty: 100, estimatePrice: 10m, buyRoomValue: 0m, spendableBuyValue: 10_000m,
            capValue: 1_000m, symFrac: 1.0, floorFrac: FloorFrac);
        Assert.Equal(25, floored);

        // With no floor (floorFrac 0), the same at-cap long is frozen out (qty 0).
        var frozen = AiBotDecisionService.ChaseSymmetricSellQty(
            100, 10m, 0m, 10_000m, 1_000m, symFrac: 1.0, floorFrac: 0.0);
        Assert.Equal(0, frozen);
    }

    [Fact]
    public void ChaseSymmetricSellQty_is_pure_and_price_guarded()
    {
        // Deterministic: same inputs ⇒ same output.
        var a = AiBotDecisionService.ChaseSymmetricSellQty(80, 7m, 210m, 500m, 2_000m, 0.5, FloorFrac);
        var b = AiBotDecisionService.ChaseSymmetricSellQty(80, 7m, 210m, 500m, 2_000m, 0.5, FloorFrac);
        Assert.Equal(a, b);
        // Non-positive price ⇒ passthrough (no divide-by-zero).
        Assert.Equal(80, AiBotDecisionService.ChaseSymmetricSellQty(80, 0m, 210m, 500m, 2_000m, 1.0, FloorFrac));
    }

    [Fact]
    public void ChaseCadenceDue_off_when_interval_le_one()
    {
        // intervalTicks <= 1 ⇒ always due (feature off) for every bot/tick.
        for (long t = 0; t < 50; t++)
        {
            Assert.True(AiBotDecisionService.ChaseCadenceDue(7, t, 0, ChaserCadenceSalt));
            Assert.True(AiBotDecisionService.ChaseCadenceDue(7, t, 1, ChaserCadenceSalt));
        }
    }

    [Fact]
    public void ChaseCadenceDue_fires_at_most_once_per_window_and_is_deterministic()
    {
        const int interval = 12;
        for (int bot = 1; bot <= 40; bot++)
        {
            int due = 0;
            for (long t = 0; t < interval; t++)          // one full window
            {
                bool d1 = AiBotDecisionService.ChaseCadenceDue(bot, t, interval, ChaserCadenceSalt);
                bool d2 = AiBotDecisionService.ChaseCadenceDue(bot, t, interval, ChaserCadenceSalt);
                Assert.Equal(d1, d2);                    // pure / reproducible
                if (d1) due++;
            }
            Assert.Equal(1, due);                        // exactly one due slot per window per bot
        }
    }

    // Mirror of the production constant (private in AiBotDecisionService); kept in sync by these tests.
    private const int ChaserCadenceSalt = 0x3B9F;

    [Fact]
    public void ChaserProbe_per_side_net_and_gross()
    {
        ChaserProbe.Configure(true);
        try
        {
            ChaserProbe.Drain();                          // clear any prior state
            ChaserProbe.RecordOrder(isBuy: true,  notional: 100m);
            ChaserProbe.RecordOrder(isBuy: false, notional: 40m);
            ChaserProbe.RecordOrder(isBuy: false, notional: 60m);
            ChaserProbe.RecordSuppressed(isBuy: true);
            ChaserProbe.RecordSuppressed(isBuy: false);

            var d = ChaserProbe.Drain();
            Assert.Equal(1, d.buyOrders);
            Assert.Equal(2, d.sellOrders);
            Assert.Equal(1, d.buySuppressed);
            Assert.Equal(1, d.sellSuppressed);
            Assert.Equal(100.0, d.buyNotional);
            Assert.Equal(100.0, d.sellNotional);          // 40 + 60
            Assert.Equal(0.0, d.netNotional);             // buy − sell (drift discriminator)
            Assert.Equal(200.0, d.grossNotional);         // buy + sell (volume discriminator)

            // Drain reset: a second drain is empty.
            var empty = ChaserProbe.Drain();
            Assert.Equal(0, empty.buyOrders);
            Assert.Equal(0.0, empty.grossNotional);
        }
        finally { ChaserProbe.Configure(false); }
    }

    [Fact]
    public void ChaserProbe_disabled_is_noop()
    {
        ChaserProbe.Configure(false);
        ChaserProbe.RecordOrder(true, 999m);
        ChaserProbe.RecordSuppressed(false);
        var d = ChaserProbe.Drain();
        Assert.Equal(0, d.buyOrders);
        Assert.Equal(0, d.sellOrders);
        Assert.Equal(0.0, d.grossNotional);
    }
}
