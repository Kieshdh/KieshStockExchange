using KieshStockExchange.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 P5b short-bracket cash-pool sizing/lag math. Pure oracle — the ConservationProbe can't see a
/// conservation-clean structural defect, so the pool/cushion arithmetic is pinned here.
/// </summary>
public class ShortBracketMathTests
{
    [Theory]
    [InlineData(true, 110, 105, 0, 110)]    // stop-limit → the limit price is the worst case
    [InlineData(false, 0, 100, 10, 110)]    // capped market → trigger 100 × (1 + 10%) = 110
    [InlineData(false, 0, 120, 5, 126)]     // trigger 120 × 1.05 = 126
    public void SlWorst_boundsTheBuyback(bool isStopLimit, decimal limit, decimal stop, decimal pct, decimal expected)
        => Assert.Equal(expected, ShortBracketMath.SlWorst(isStopLimit, limit, stop, pct));

    [Fact]
    public void Pool_isWorstCaseTimesHeld()
        => Assert.Equal(3300m, ShortBracketMath.Pool(110m, 30));   // SL_worst 110 × 30 short shares

    [Fact]
    public void CushionFreed_isWorstVsActualTimesCover()
    {
        // Ultraplan's worked number: N=30, SL_worst=110, a TP closes 10 @ 90 ⇒ poolDrop 1100 =
        // buyback 900 + cushion 200. Cushion = (110 − 90) × 10 = 200.
        Assert.Equal(200m, ShortBracketMath.CushionFreed(slWorst: 110m, fillPrice: 90m, coverQty: 10));
    }

    [Fact]
    public void CushionFreed_neverNegative()
        => Assert.Equal(0m, ShortBracketMath.CushionFreed(slWorst: 100m, fillPrice: 105m, coverQty: 5));

    [Fact]
    public void PoolDrop_equals_buyback_plus_cushion()
    {
        // Invariant check the coordinator relies on: poolDrop (SL_worst·cover) == buyback (fill·cover) + cushion.
        decimal slWorst = 110m, fill = 90m; int cover = 10;
        decimal poolDrop = ShortBracketMath.Pool(slWorst, cover);          // 1100
        decimal buyback = fill * cover;                                    // 900
        decimal cushion = ShortBracketMath.CushionFreed(slWorst, fill, cover); // 200
        Assert.Equal(poolDrop, buyback + cushion);
    }
}
