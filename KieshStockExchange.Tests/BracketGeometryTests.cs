using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketEngineServices;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 P4 / §P5b bracket geometry — long (sell legs) and short (buy-to-close legs) are mirror images.
/// Long: SL below entry, TPs above &amp; ascending. Short: SL above entry, TPs below &amp; descending.
/// Both: Σ TP qty ≤ parent qty, every TP qty &gt; 0.
/// </summary>
public class BracketGeometryTests
{
    private const CurrencyType USD = CurrencyType.USD;
    private static (decimal, int)[] Tp(params (decimal, int)[] xs) => xs;

    // ---- Long (unchanged behavior) ----
    [Fact]
    public void Long_valid_slBelow_tpsAscendingAbove()
        => Assert.Null(BracketGeometryValidator.Validate(100m, 90m, Tp((110m, 2), (120m, 3)), 5, USD, isShort: false));

    [Fact]
    public void Long_rejects_slAboveEntry()
        => Assert.NotNull(BracketGeometryValidator.Validate(100m, 105m, Tp((110m, 2)), 5, USD, isShort: false));

    [Fact]
    public void Long_rejects_tpBelowEntry()
        => Assert.NotNull(BracketGeometryValidator.Validate(100m, 90m, Tp((95m, 2)), 5, USD, isShort: false));

    [Fact]
    public void Long_rejects_tpsNotAscending()
        => Assert.NotNull(BracketGeometryValidator.Validate(100m, 90m, Tp((120m, 2), (110m, 2)), 5, USD, isShort: false));

    // ---- Short (mirror) ----
    [Fact]
    public void Short_valid_slAbove_tpsDescendingBelow()
        => Assert.Null(BracketGeometryValidator.Validate(100m, 110m, Tp((90m, 2), (80m, 3)), 5, USD, isShort: true));

    [Fact]
    public void Short_rejects_slBelowEntry()
        => Assert.NotNull(BracketGeometryValidator.Validate(100m, 95m, Tp((90m, 2)), 5, USD, isShort: true));

    [Fact]
    public void Short_rejects_tpAboveEntry()
        => Assert.NotNull(BracketGeometryValidator.Validate(100m, 110m, Tp((105m, 2)), 5, USD, isShort: true));

    [Fact]
    public void Short_rejects_tpsNotDescending()
        => Assert.NotNull(BracketGeometryValidator.Validate(100m, 110m, Tp((80m, 2), (90m, 2)), 5, USD, isShort: true));

    // ---- Both sides ----
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsTpSumOverParent(bool isShort)
    {
        var sl = isShort ? 110m : 90m;
        var tps = isShort ? Tp((90m, 4), (80m, 4)) : Tp((110m, 4), (120m, 4));
        Assert.NotNull(BracketGeometryValidator.Validate(100m, sl, tps, 5, USD, isShort));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsNonPositiveTpQty(bool isShort)
    {
        var sl = isShort ? 110m : 90m;
        var tps = isShort ? Tp((90m, 0)) : Tp((110m, 0));
        Assert.NotNull(BracketGeometryValidator.Validate(100m, sl, tps, 5, USD, isShort));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TpOnly_noStop_isValid(bool isShort)
    {
        var tps = isShort ? Tp((90m, 2)) : Tp((110m, 2));
        Assert.Null(BracketGeometryValidator.Validate(100m, null, tps, 5, USD, isShort));
    }
}
