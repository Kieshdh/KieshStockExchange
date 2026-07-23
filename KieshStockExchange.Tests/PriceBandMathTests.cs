using KieshStockExchange.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §geometric price-bands: pins the log-symmetric bound primitive. A cap magnitude C ⇒ factor F=1+C, price
/// bounded to [anchor/F, anchor×F] (so "200%" = ×3 up / ÷3 down). Tests: factor mapping, log-symmetry
/// (lo·hi = anchor²), multiplicative composition (the guard fix), the veto table, unconfigured inertness,
/// and up-side parity with the old linear method (the byte-identical rollback contract at current dials).
/// </summary>
public class PriceBandMathTests
{
    [Fact]
    public void Factor_maps_cap_to_geometric_factor()
    {
        Assert.Equal(3.0m, PriceBandMath.Factor(2.0m));    // 200% ⇒ ×3
        Assert.Equal(1.20m, PriceBandMath.Factor(0.20m));  // 20%  ⇒ ×1.2
        Assert.Equal(1.50m, PriceBandMath.Factor(0.50m));  // 50%  ⇒ ×1.5
        Assert.Equal(1.0m, PriceBandMath.Factor(0m));      // unset ⇒ no band
        Assert.Equal(1.0m, PriceBandMath.Factor(-0.3m));   // negative ⇒ clamped to no band
    }

    [Theory]
    [InlineData(100, 2.0, 50, 200)]   // ÷2 .. ×2
    [InlineData(100, 4.0, 25, 400)]   // ÷4 .. ×4
    [InlineData(90, 3.0, 30, 270)]    // ÷3 .. ×3 (clean thirds)
    public void Band_is_the_geometric_interval(decimal anchor, decimal f, decimal lo, decimal hi)
    {
        var (gotLo, gotHi) = PriceBandMath.Band(anchor, f);
        Assert.Equal(lo, gotLo);
        Assert.Equal(hi, gotHi);
    }

    [Theory]
    [InlineData(100, 2.0)]
    [InlineData(100, 4.0)]
    [InlineData(250, 5.0)]
    public void Band_is_log_symmetric_lo_times_hi_equals_anchor_squared(decimal anchor, decimal f)
    {
        var (lo, hi) = PriceBandMath.Band(anchor, f);
        Assert.Equal(anchor * anchor, lo * hi);   // ×F up and ÷F down are the SAME log-distance
    }

    [Fact]
    public void Band_is_degenerate_when_unconfigured()
    {
        Assert.Equal((100m, 100m), PriceBandMath.Band(100m, 1m));   // F=1 ⇒ point band
        Assert.Equal((0m, 0m), PriceBandMath.Band(0m, 3m));         // anchor≤0 ⇒ degenerate
    }

    [Fact]
    public void Compose_multiplies_factors_not_sums_them()
    {
        // The guard fix: nested caps stack as (1+Band)(1+Cap), NOT Band+Cap. 0.12 & 0.30 ⇒ 1.456, not 1.42.
        Assert.Equal(1.12m * 1.30m, PriceBandMath.Compose(0.12m, 0.30m));
        Assert.Equal(1.456m, PriceBandMath.Compose(0.12m, 0.30m));
    }

    [Theory]
    // anchor 90, F 3 ⇒ up-veto above 270, down-veto below 30
    [InlineData(271, true, true)]    // buy above ×3 ⇒ vetoed
    [InlineData(269, true, false)]   // buy inside band ⇒ ok
    [InlineData(270, true, false)]   // buy AT the boundary ⇒ ok (strict >)
    [InlineData(29, false, true)]    // sell below ÷3 ⇒ vetoed
    [InlineData(31, false, false)]   // sell inside band ⇒ ok
    [InlineData(30, false, false)]   // sell AT the boundary ⇒ ok (strict <)
    public void IsOver_veto_table(decimal mkt, bool isBuy, bool expected)
        => Assert.Equal(expected, PriceBandMath.IsOver(mkt, anchor: 90m, f: 3m, isBuy: isBuy));

    [Theory]
    [InlineData(1.0)]    // F≤1 ⇒ cap unset ⇒ no veto (matches linear `cap>0` gate)
    [InlineData(0.5)]
    public void IsOver_never_vetoes_when_band_unconfigured(decimal f)
    {
        Assert.False(PriceBandMath.IsOver(1000m, 100m, f, isBuy: true));
        Assert.False(PriceBandMath.IsOver(1m, 100m, f, isBuy: false));
    }

    [Fact]
    public void IsOver_no_veto_on_bad_inputs()
    {
        Assert.False(PriceBandMath.IsOver(150m, anchor: 0m, f: 3m, isBuy: true));   // anchor≤0
        Assert.False(PriceBandMath.IsOver(0m, anchor: 100m, f: 3m, isBuy: false));  // mkt≤0
    }

    [Fact]
    public void Up_side_matches_linear_at_same_cap()
    {
        // Byte-identical contract on the BINDING (up) side: geometric up-bound anchor×(1+cap) == linear up-bound.
        // (The down side deliberately differs — geometric ÷F tightens the floor vs linear (1−cap); the A/B quantifies it.)
        AssertUpSideMatchesLinear(100m, 0.20m);
        AssertUpSideMatchesLinear(100m, 0.30m);
        AssertUpSideMatchesLinear(90m, 2.0m);
    }

    private static void AssertUpSideMatchesLinear(decimal anchor, decimal cap)
    {
        var (_, hi) = PriceBandMath.Band(anchor, PriceBandMath.Factor(cap));
        Assert.Equal(anchor * (1m + cap), hi);
    }

    [Fact]
    public void FundamentalService_read_band_geometric_removes_the_additive_span_down_bias()
    {
        // §log-sym #3 (FundamentalService read-time excursion band): the legacy floor is the LINEAR ADDITIVE span
        // seed·(1 − (Band+ShockCap+CoMoveShiftCap)); the fix uses the geometric compose F=(1+Band)(1+ShockCap)
        // (1+CoMoveShiftCap), floor seed/F. At the live dials the geometric floor sits ABOVE the linear one (less
        // down-room = the down-bias removed) and is ratio-symmetric with the ceiling.
        const decimal seed = 100m, band = 0.12m, shockCap = 0.25m, coMoveCap = 0.08m;
        var f = PriceBandMath.Factor(band) * PriceBandMath.Factor(shockCap) * PriceBandMath.Factor(coMoveCap);
        var (lo, hi) = PriceBandMath.Band(seed, f);

        decimal linearSpan = band + shockCap + coMoveCap;              // 0.45
        decimal linearLo = seed * (1m - linearSpan);                  // 55
        Assert.True(lo > linearLo, $"geometric floor {lo} should sit above the linear floor {linearLo}");
        Assert.Equal(seed * seed, lo * hi);                          // ratio-symmetric (up log-dist == down log-dist)
        Assert.Equal(seed * f, hi);                                  // ceiling = seed·F (the binding up side)
    }
}
