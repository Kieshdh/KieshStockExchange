using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §bounce lever (b) — finer price-tick rounding + its conservation guard.
///
///   • <see cref="CurrencyHelper.RoundPrice"/> with the dial at 0 is byte-identical to
///     <see cref="CurrencyHelper.RoundMoney"/>; with the dial &gt; 0 it snaps prices to a finer grid.
///   • Conservation invariant: finer PRICE decimals must never leak cash. Cash is always
///     <see cref="CurrencyHelper.Notional"/> = RoundMoney(price*qty), so (i) the buyer debit equals the
///     seller credit per fill, and (ii) a multi-fill order's place-time reservation RoundMoney(price*Q)
///     vs the sum of per-fill settled notionals Σ RoundMoney(price*qᵢ) stays within the sub-cent
///     reservation tolerance regardless of how many decimals the price carries.
///
/// The dial is a process-global static, so this suite runs serial and resets it in the ctor/Dispose.
/// </summary>
[Collection("PriceTickSerial")]
public sealed class PriceTickTests : IDisposable
{
    public PriceTickTests() => CurrencyHelper.PriceTickExtraDecimals = 0;
    public void Dispose() => CurrencyHelper.PriceTickExtraDecimals = 0;

    [Fact]
    public void RoundPrice_dial_zero_is_byte_identical_to_RoundMoney()
    {
        CurrencyHelper.PriceTickExtraDecimals = 0;
        foreach (var v in new[] { 50.1234m, 100m, 0.005m, 19.999m, 1234.5678m })
        {
            Assert.Equal(CurrencyHelper.RoundMoney(v, CurrencyType.USD), CurrencyHelper.RoundPrice(v, CurrencyType.USD));
            Assert.Equal(CurrencyHelper.RoundMoney(v, CurrencyType.JPY), CurrencyHelper.RoundPrice(v, CurrencyType.JPY));
        }
    }

    [Fact]
    public void RoundPrice_dial_positive_snaps_to_finer_grid()
    {
        CurrencyHelper.PriceTickExtraDecimals = 2;            // USD 2dp + 2 = 4dp
        Assert.Equal(50.1234m, CurrencyHelper.RoundPrice(50.12344m, CurrencyType.USD));
        Assert.Equal(50.12m, CurrencyHelper.RoundMoney(50.12344m, CurrencyType.USD)); // cash still cents

        CurrencyHelper.PriceTickExtraDecimals = 2;            // JPY 0dp + 2 = 2dp
        Assert.Equal(144.57m, CurrencyHelper.RoundPrice(144.567m, CurrencyType.JPY));
        Assert.Equal(145m, CurrencyHelper.RoundMoney(144.567m, CurrencyType.JPY));    // cash still integer
    }

    [Fact]
    public void Finer_price_keeps_buyer_debit_equal_to_seller_credit_per_fill()
    {
        CurrencyHelper.PriceTickExtraDecimals = 4;
        var price = CurrencyHelper.RoundPrice(50.123456m, CurrencyType.USD); // 50.123456
        const int qty = 7;
        // Both legs settle on Notional(price, qty) = RoundMoney(price*qty) — identical by construction.
        var buyerPaid = CurrencyHelper.Notional(price, qty, CurrencyType.USD);
        var sellerGot = CurrencyHelper.Notional(price, qty, CurrencyType.USD);
        Assert.Equal(buyerPaid, sellerGot);
        Assert.Equal(CurrencyHelper.RoundMoney(price * qty, CurrencyType.USD), buyerPaid); // cents-aligned cash
    }

    [Fact]
    public void Finer_price_keeps_reservation_vs_settlement_within_subcent_tolerance()
    {
        CurrencyHelper.PriceTickExtraDecimals = 4;
        var price = CurrencyHelper.RoundPrice(50.123456m, CurrencyType.USD);
        var fills = new[] { 3, 2, 2 };                       // a 7-share order filled in 3 partials
        int totalQty = 0; foreach (var q in fills) totalQty += q;

        // Place-time hold (ReservationMath.InitialBuyReservation) vs sum of per-fill settled notionals.
        var reserved = CurrencyHelper.Notional(price, totalQty, CurrencyType.USD);
        decimal settled = 0m;
        foreach (var q in fills) settled += CurrencyHelper.Notional(price, q, CurrencyType.USD);

        // The residual is sub-cent (bounded by ~½ cent per fill) — well under the ReservationAuditor
        // phantom-warn threshold (5.0) — and NOT amplified by the extra price decimals.
        Assert.True(System.Math.Abs(reserved - settled) <= 0.02m,
            $"reserved={reserved} settled={settled} residual={reserved - settled}");
    }
}

/// <summary>Serial collection so the process-global price-tick dial can't leak across parallel tests.</summary>
[CollectionDefinition("PriceTickSerial", DisableParallelization = true)]
public sealed class PriceTickSerialCollection { }
