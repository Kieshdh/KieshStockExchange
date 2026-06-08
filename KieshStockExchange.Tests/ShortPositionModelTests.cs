using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 Patch 1 — model-level invariants for cash-collateralized shorts. These exercise the
/// pure domain math (Position/Fund) that the settlement engine relies on, with no DB. The
/// deeper engine + Postgres conservation soak is the manual verification step in the plan.
/// </summary>
public class ShortPositionModelTests
{
    private const CurrencyType Usd = CurrencyType.USD;

    private static Position FlatPosition() => new() { UserId = 1, StockId = 1 };

    // ---- Position invariants -------------------------------------------------

    [Fact]
    public void ApplyDelta_pushes_quantity_negative_and_stays_valid_as_short()
    {
        var p = FlatPosition();
        p.ApplyDelta(-50);
        p.TakeShortCollateral(5000m, Usd);

        Assert.Equal(-50, p.Quantity);
        Assert.True(p.IsShort);
        Assert.Equal(5000m, p.ShortCollateral);
        Assert.Equal(Usd, p.ShortCollateralCurrency);
        Assert.True(p.IsValid());
    }

    [Fact]
    public void Short_with_share_reservation_is_invalid()
    {
        var p = FlatPosition();
        p.Quantity = -10;          // short
        p.ReservedQuantity = 5;    // shares reserved — illegal on a short
        Assert.False(p.IsValid());
    }

    [Fact]
    public void Long_with_short_collateral_is_invalid()
    {
        var p = FlatPosition();
        p.Quantity = 10;           // long
        p.ShortCollateral = 100m;  // collateral on a long — illegal
        Assert.False(p.IsValid());
    }

    [Fact]
    public void ApplyDelta_into_negative_rejects_existing_share_reservation()
    {
        var p = FlatPosition();
        p.Quantity = 5;
        p.ReservedQuantity = 5;
        Assert.Throws<InvalidOperationException>(() => p.ApplyDelta(-10));
    }

    [Fact]
    public void ReleaseShortCollateral_cannot_release_more_than_held()
    {
        var p = FlatPosition();
        p.ApplyDelta(-10);
        p.TakeShortCollateral(1000m, Usd);
        Assert.Throws<ArgumentException>(() => p.ReleaseShortCollateral(1000.01m));
    }

    [Fact]
    public void TakeShortCollateral_rejects_mixed_currency()
    {
        var p = FlatPosition();
        p.ApplyDelta(-10);
        p.TakeShortCollateral(1000m, CurrencyType.USD);
        Assert.Throws<InvalidOperationException>(() => p.TakeShortCollateral(500m, CurrencyType.EUR));
    }

    // ---- Fund: opening a fully-collateralized short is buying-power-neutral --

    [Fact]
    public void Opening_short_leaves_available_balance_unchanged()
    {
        var fund = new Fund { UserId = 1, CurrencyType = Usd, TotalBalance = 1000m };
        var availBefore = fund.AvailableBalance;

        // Proceeds credited, then equal collateral reserved (fill price == anchor).
        var notional = CurrencyHelper.Notional(20m, 50, Usd); // 50 @ 20 = 1000
        fund.TotalBalance += notional;
        fund.ReserveFunds(notional);

        Assert.Equal(availBefore, fund.AvailableBalance);
        Assert.Equal(notional, fund.ReservedBalance);
    }

    // ---- Two-party conservation: open then buy-to-close ---------------------

    [Fact]
    public void Open_then_close_conserves_cash_and_shares_and_realizes_pnl()
    {
        // Party A opens a short, party B is the counterparty (buyer at open, seller at close).
        var aFund = new Fund { UserId = 1, CurrencyType = Usd, TotalBalance = 500m };
        var bFund = new Fund { UserId = 2, CurrencyType = Usd, TotalBalance = 10_000m };
        var aPos = new Position { UserId = 1, StockId = 1 };
        var bPos = new Position { UserId = 2, StockId = 1, Quantity = 1000 };

        decimal TotalCash() => aFund.TotalBalance + bFund.TotalBalance;
        int NetShares() => aPos.Quantity + bPos.Quantity;

        var cash0 = TotalCash();
        var shares0 = NetShares();

        // --- Open: A short-sells 50 @ 20 to B ---
        const int qty = 50;
        const decimal openPx = 20m;
        var openNotional = CurrencyHelper.Notional(openPx, qty, Usd);

        // B buys: pays, gains shares
        bFund.TotalBalance -= openNotional;
        bPos.Quantity += qty;
        // A shorts: credited proceeds, reserves equal collateral, goes negative
        aFund.TotalBalance += openNotional;
        aFund.ReserveFunds(openNotional);
        aPos.ApplyDelta(-qty);
        aPos.TakeShortCollateral(openNotional, Usd);

        Assert.Equal(cash0, TotalCash());        // cash conserved
        Assert.Equal(shares0, NetShares());      // shares conserved
        Assert.Equal(-qty, aPos.Quantity);
        Assert.Equal(openNotional, aPos.ShortCollateral);

        // --- Close: A buys-to-close 50 @ 16 from B (price dropped → profit) ---
        const decimal closePx = 16m;
        var closeNotional = CurrencyHelper.Notional(closePx, qty, Usd);

        // A buys back: pays, position toward zero, releases collateral
        var qtyBefore = aPos.Quantity;
        aFund.TotalBalance -= closeNotional;
        aPos.ApplyDelta(qty);
        // full cover → release all remaining collateral
        var release = aPos.ShortCollateral;
        aFund.UnreserveFunds(release);
        aPos.ReleaseShortCollateral(release);
        // B sells: credited
        bFund.TotalBalance += closeNotional;
        bPos.Quantity -= qty;

        Assert.Equal(cash0, TotalCash());        // cash still conserved
        Assert.Equal(shares0, NetShares());      // shares still conserved
        Assert.Equal(0, aPos.Quantity);          // flat again
        Assert.Equal(0m, aPos.ShortCollateral);  // collateral fully released
        Assert.Equal(0m, aFund.ReservedBalance); // no phantom reserve
        Assert.True(qtyBefore < 0);

        // Realized P/L = qty × (openPx − closePx) = 50 × 4 = 200
        Assert.Equal(500m + qty * (openPx - closePx), aFund.TotalBalance);
    }

    // ---- Randomized property test: invariants hold over many open/close steps -

    [Fact]
    public void Random_open_close_sequences_preserve_invariants()
    {
        var rng = new Random(12345); // seeded → reproducible

        for (int trial = 0; trial < 500; trial++)
        {
            var aFund = new Fund { UserId = 1, CurrencyType = Usd, TotalBalance = 100_000m };
            var bFund = new Fund { UserId = 2, CurrencyType = Usd, TotalBalance = 100_000m };
            var aPos = new Position { UserId = 1, StockId = 1 };
            var bPos = new Position { UserId = 2, StockId = 1, Quantity = 100_000 };

            var cash0 = aFund.TotalBalance + bFund.TotalBalance;
            var shares0 = aPos.Quantity + bPos.Quantity;

            int openQty = 0;          // A's current short size (positive number)
            decimal collateral = 0m;  // collateral held against the short

            int steps = rng.Next(1, 12);
            for (int s = 0; s < steps; s++)
            {
                var px = Math.Round((decimal)(5 + rng.NextDouble() * 40), 2);

                if (openQty == 0 || rng.NextDouble() < 0.5)
                {
                    // OPEN / extend not allowed in MVP, so only open when flat
                    if (openQty != 0) continue;
                    var q = rng.Next(1, 200);
                    var notional = CurrencyHelper.Notional(px, q, Usd);
                    bFund.TotalBalance -= notional;
                    bPos.Quantity += q;
                    aFund.TotalBalance += notional;
                    aFund.ReserveFunds(notional);
                    aPos.ApplyDelta(-q);
                    aPos.TakeShortCollateral(notional, Usd);
                    openQty = q;
                    collateral += notional;
                }
                else
                {
                    // CLOSE a random portion (never more than open — clamp)
                    var q = Math.Min(openQty, rng.Next(1, openQty + 1));
                    var notional = CurrencyHelper.Notional(px, q, Usd);
                    var qtyBefore = aPos.Quantity;
                    aFund.TotalBalance -= notional;
                    aPos.ApplyDelta(q);
                    var release = aPos.Quantity >= 0
                        ? aPos.ShortCollateral
                        : CurrencyHelper.RoundMoney(aPos.ShortCollateral * q / -qtyBefore, Usd);
                    release = Math.Min(release, aPos.ShortCollateral);
                    aFund.UnreserveFunds(release);
                    aPos.ReleaseShortCollateral(release);
                    bFund.TotalBalance += notional;
                    bPos.Quantity -= q;
                    openQty -= q;
                    collateral -= release;
                }

                // Invariants after every step:
                Assert.Equal(cash0, aFund.TotalBalance + bFund.TotalBalance);          // cash conserved
                Assert.Equal(shares0, aPos.Quantity + bPos.Quantity);                  // shares conserved
                Assert.True(aPos.ShortCollateral >= 0m);                               // never negative
                Assert.Equal(-openQty, aPos.Quantity);                                 // qty tracks short size
                Assert.True(aPos.IsValid());
                Assert.True(bPos.IsValid());
                Assert.True(aFund.IsValid());
                Assert.True(bFund.IsValid());
                if (openQty == 0)
                    Assert.Equal(0m, aPos.ShortCollateral);                            // flat → no collateral
            }
        }
    }
}
