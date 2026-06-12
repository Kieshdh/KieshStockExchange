using KieshStockExchange.Models;
using KieshStockExchange.Services.DataServices.Persistence;

namespace KieshStockExchange.Tests;

/// <summary>
/// Round 2 §0013: locks the flag-off byte-identical contract for the bracket-flip patch series.
///
/// The complete golden-hash 20-tick scenario needs a heavy AiBotContext fixture (mock
/// IMarketDataService + IAccountsCache + IOrderBookEngine etc.) that's a separate harness.
/// These tests pin the invariants that the FULL golden-stream test would also pin — so they
/// catch the common regressions (a new flag accidentally bleeding into the cold path, a
/// FlipQuantity assignment leaking onto a plain order, a Path-1-minimal callable producing
/// a non-zero FlipQuantity) without that fixture.
///
/// IF a soak shows the flag-off behaviour has drifted vs 5fafc0c, the full golden-hash
/// harness is the next test to author (see cover note §determinism for the planned shape).
/// </summary>
public class BracketFlipDeterminismTests
{
    /// <summary>Path-2 default: an Order with no FlipQuantity assignment carries 0.</summary>
    [Fact]
    public void Order_default_FlipQuantity_is_zero()
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 10, Price = 100m,
            Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        };
        Assert.Equal(0, o.FlipQuantity);
    }

    /// <summary>Negative FlipQuantity is rejected (clamped to 0).</summary>
    [Fact]
    public void Order_FlipQuantity_clamps_negative_to_zero()
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 10, Price = 100m,
            Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
            FlipQuantity = -5,
        };
        Assert.Equal(0, o.FlipQuantity);
    }

    /// <summary>FlipQuantity round-trips through OrderRow ↔ Order without loss.</summary>
    [Fact]
    public void Order_FlipQuantity_persists_through_mapper()
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 20, Price = 50m,
            Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
            FlipQuantity = 7,
        };
        var row = OrderMapper.ToRow(o);
        Assert.Equal(7, row.FlipQuantity);
        var back = OrderMapper.ToDomain(row);
        Assert.Equal(7, back.FlipQuantity);
    }

    /// <summary>FlipQuantity survives Order.Clone() / Order.CloneFull() — bracket coordinator
    /// reads cloned/canonical refs.</summary>
    [Fact]
    public void Order_FlipQuantity_survives_clone()
    {
        var o = new Order
        {
            UserId = 1, StockId = 1, Quantity = 12, Price = 80m,
            Side = OrderSide.Sell, Entry = EntryType.Market, Stop = StopKind.None,
            FlipQuantity = 5,
        };
        Assert.Equal(5, o.Clone().FlipQuantity);
        var deep = o.CloneFull();
        Assert.Equal(5, deep.FlipQuantity);
    }

    /// <summary>AIUser.RoundtripBiasPrc defaults to 0.5 (neutral) so an un-regenerated bot
    /// from a pre-round-2 workbook produces the same bracket-qty draw under _bracketFlip.</summary>
    [Fact]
    public void AIUser_RoundtripBiasPrc_default_is_neutral()
    {
        var u = new AIUser();
        Assert.Equal(0.5m, u.RoundtripBiasPrc);
    }

    /// <summary>AIUser.RoundtripBiasPrc honours the [0,1] invariant (RequiredPrc).</summary>
    [Fact]
    public void AIUser_RoundtripBiasPrc_rejects_out_of_range()
    {
        var u = new AIUser();
        Assert.Throws<ArgumentOutOfRangeException>(() => u.RoundtripBiasPrc = -0.1m);
        Assert.Throws<ArgumentOutOfRangeException>(() => u.RoundtripBiasPrc = 1.01m);
    }
}
