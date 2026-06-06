using KieshStockExchange.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §3.6 P5 trailing-stop math. Pure, deterministic oracle for the watcher's per-tick logic — the
/// ConservationProbe can't see trailing behavior, so this is where the trigger semantics are pinned.
/// </summary>
public class TrailMathTests
{
    [Theory]
    // sell-trail (protect long): trigger = watermark − distance
    [InlineData(103, 2, false, false, 101)]
    [InlineData(100, 10, true, false, 90)]   // 10% of 100
    // buy-trail (protect short): trigger = watermark + distance
    [InlineData(100, 2, false, true, 102)]
    [InlineData(100, 10, true, true, 110)]
    public void EffectiveStop_isSidedAndOffsetAware(decimal watermark, decimal offset, bool pct, bool isBuy, decimal expected)
        => Assert.Equal(expected, TrailMath.EffectiveStop(watermark, offset, pct, isBuy));

    [Fact]
    public void Ratchet_sell_movesUpOnly()
    {
        Assert.Equal(103m, TrailMath.Ratchet(100m, 103m, isBuy: false)); // new high
        Assert.Equal(103m, TrailMath.Ratchet(103m, 101m, isBuy: false)); // never retreats
    }

    [Fact]
    public void Ratchet_buy_movesDownOnly()
    {
        Assert.Equal(97m, TrailMath.Ratchet(100m, 97m, isBuy: true));  // new low
        Assert.Equal(97m, TrailMath.Ratchet(97m, 99m, isBuy: true));   // never retreats
    }

    [Fact]
    public void Crossed_firesOnTheCorrectSide()
    {
        Assert.True(TrailMath.Crossed(price: 99m, effectiveStop: 101m, isBuy: false));   // drop through sell stop
        Assert.False(TrailMath.Crossed(price: 102m, effectiveStop: 101m, isBuy: false));
        Assert.True(TrailMath.Crossed(price: 111m, effectiveStop: 110m, isBuy: true));   // rise through buy stop
        Assert.False(TrailMath.Crossed(price: 109m, effectiveStop: 110m, isBuy: true));
    }

    [Fact]
    public void SellTrail_endToEnd_ratchetsThenFires()
    {
        // off=2 abs; ticks 100 → 103 → 101 → 99.
        decimal wm = 100m, off = 2m;
        foreach (var price in new[] { 103m, 101m, 99m })
            wm = TrailMath.Ratchet(wm, price, isBuy: false);
        Assert.Equal(103m, wm);                                   // watermark caught the high, ignored the dip
        var eff = TrailMath.EffectiveStop(wm, off, false, false);
        Assert.Equal(101m, eff);
        Assert.True(TrailMath.Crossed(99m, eff, isBuy: false));   // 99 ≤ 101 ⇒ fires
        Assert.False(TrailMath.Crossed(101.5m, eff, isBuy: false));
    }

    [Fact]
    public void StaleWatermark_yieldsLooserStop_neverEarlierFire()
    {
        // §P5 staleness contract: a restart restores a watermark ≤ the live one (monotonic), so the
        // restored sell stop is at or below the live stop — it can only fire later, never earlier.
        decimal off = 2m;
        decimal liveWm = 110m, staleWm = 105m;  // stale persisted value lags the true high
        decimal liveStop = TrailMath.EffectiveStop(liveWm, off, false, false);   // 108
        decimal staleStop = TrailMath.EffectiveStop(staleWm, off, false, false); // 103
        Assert.True(staleStop <= liveStop);
        // A price that would fire live (≤108) at 104 does NOT fire under the stale stop (103).
        Assert.True(TrailMath.Crossed(104m, liveStop, isBuy: false));
        Assert.False(TrailMath.Crossed(104m, staleStop, isBuy: false));
    }
}
