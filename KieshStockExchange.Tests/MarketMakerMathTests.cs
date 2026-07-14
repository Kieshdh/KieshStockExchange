using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §mm-cohort determinism + invariant tests for <see cref="MarketMakerMath"/> — the pure quote math behind the
/// all-weather market-maker cohort. These pin the whole behavioural surface of the quote function without
/// standing up the engine (same idiom as <see cref="BotStaggeringDeterminismTests"/> /
/// DirectionalBiasTests). The conservation-critical contract — the MM's own quotes can never grow |inventory|
/// past the cap — is fully expressible here; the engine-level ConservationProbe covers the settlement path.
/// </summary>
public class MarketMakerMathTests
{
    private static MmConfig Cfg(decimal halfBps = 15m, int size = 10, decimal skewBps = 20m,
        decimal jitterBps = 0m, decimal oneSidedWiden = 2.0m, bool useMicro = false) =>
        new(Enabled: true, HalfSpreadBps: halfBps, QuoteSize: size, SkewBps: skewBps,
            RequoteThresholdBps: 5m, MaxCashFrac: 0.5m, PriceJitterBps: jitterBps,
            OneSidedWidenMult: oneSidedWiden, UseMicro: useMicro);

    // ---- Reference ladder ------------------------------------------------------------------------

    [Fact]
    public void Reference_two_sided_touch_is_mid_and_not_one_sided()
    {
        var r = MarketMakerMath.Reference(bestBid: 100m, bestAsk: 102m, bidQty: 5, askQty: 5,
            lastTrade: 50m, ewma: 60m, seed: 70m, useMicro: false, out var oneSided);
        Assert.Equal(101m, r);
        Assert.False(oneSided);
    }

    [Fact]
    public void Reference_micro_weights_toward_the_thicker_opposite_queue()
    {
        // Larger ask queue pulls the micro-price toward the bid (Stoikov micro-price weighting).
        var r = MarketMakerMath.Reference(bestBid: 100m, bestAsk: 102m, bidQty: 1, askQty: 9,
            lastTrade: 0m, ewma: 0m, seed: 0m, useMicro: true, out _);
        Assert.Equal((100m * 9 + 102m * 1) / 10m, r);
    }

    [Fact]
    public void Reference_falls_through_last_then_ewma_then_seed_when_book_is_one_sided()
    {
        // Up-shock: asks empty (bestAsk 0). Must NOT require a two-sided book.
        Assert.Equal(55m, MarketMakerMath.Reference(100m, 0m, 5, 0, lastTrade: 55m, ewma: 60m, seed: 70m, useMicro: false, out var os1));
        Assert.True(os1);
        Assert.Equal(60m, MarketMakerMath.Reference(0m, 0m, 0, 0, lastTrade: 0m, ewma: 60m, seed: 70m, useMicro: false, out _));
        Assert.Equal(70m, MarketMakerMath.Reference(0m, 0m, 0, 0, lastTrade: 0m, ewma: 0m, seed: 70m, useMicro: false, out _));
        Assert.Equal(0m,  MarketMakerMath.Reference(0m, 0m, 0, 0, lastTrade: 0m, ewma: 0m, seed: 0m,  useMicro: false, out _));
    }

    // ---- The up-shock requirement: an ask is produced with NO resting asks ----------------------

    [Fact]
    public void Quote_produces_an_ask_into_a_one_sided_book_from_flat_inventory()
    {
        var reference = MarketMakerMath.Reference(100m, 0m, 5, 0, lastTrade: 100m, ewma: 0m, seed: 0m, useMicro: false, out var oneSided);
        var q = MarketMakerMath.Quote(reference, oneSided, inv: 0, cap: 100, CurrencyType.USD, Cfg(), aiUserId: 1, stockId: 1);
        Assert.True(q.Ask.Qty > 0);
        Assert.True(q.Ask.Price > 0m);
        Assert.True(q.Bid.Qty > 0);
        Assert.True(q.Ask.Price > q.Bid.Price); // never locked/crossed
    }

    [Fact]
    public void Quote_widens_the_spread_when_reference_is_one_sided()
    {
        var twoSided = MarketMakerMath.Quote(100m, oneSided: false, inv: 0, cap: 100, CurrencyType.USD, Cfg(jitterBps: 0m), 1, 1);
        var oneSided = MarketMakerMath.Quote(100m, oneSided: true,  inv: 0, cap: 100, CurrencyType.USD, Cfg(jitterBps: 0m), 1, 1);
        var twoSidedSpread = twoSided.Ask.Price - twoSided.Bid.Price;
        var oneSidedSpread = oneSided.Ask.Price - oneSided.Bid.Price;
        Assert.True(oneSidedSpread > twoSidedSpread);
    }

    // ---- Inventory skew + the hard two-sided cap (conservation-critical) -------------------------

    [Fact]
    public void Long_inventory_shrinks_the_bid_and_lowers_both_quotes()
    {
        var flat = MarketMakerMath.Quote(100m, false, inv: 0,  cap: 100, CurrencyType.USD, Cfg(jitterBps: 0m), 1, 1);
        var lng  = MarketMakerMath.Quote(100m, false, inv: 50, cap: 100, CurrencyType.USD, Cfg(jitterBps: 0m), 1, 1);
        Assert.True(lng.Bid.Qty < flat.Bid.Qty);          // shrink the side that would grow the long
        Assert.True(lng.Bid.Price < flat.Bid.Price);      // skew both quotes down to encourage selling
        Assert.True(lng.Ask.Price < flat.Ask.Price);
    }

    [Fact]
    public void At_or_beyond_the_long_cap_no_bid_is_posted_and_ask_still_supplies()
    {
        var q = MarketMakerMath.Quote(100m, false, inv: 100, cap: 100, CurrencyType.USD, Cfg(), 1, 1);
        Assert.Equal(0, q.Bid.Qty);     // cannot grow the long past the cap
        Assert.True(q.Ask.Qty > 0);     // but still offers to reduce it
    }

    [Fact]
    public void At_or_beyond_the_short_cap_no_ask_is_posted_and_bid_still_supplies()
    {
        var q = MarketMakerMath.Quote(100m, false, inv: -100, cap: 100, CurrencyType.USD, Cfg(), 1, 1);
        Assert.Equal(0, q.Ask.Qty);
        Assert.True(q.Bid.Qty > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(-40)]
    [InlineData(99)]
    [InlineData(-99)]
    public void Own_quotes_can_never_grow_inventory_past_the_cap(int inv)
    {
        const int cap = 100;
        var q = MarketMakerMath.Quote(100m, false, inv, cap, CurrencyType.USD, Cfg(size: 1000), 1, 1);
        Assert.True(inv + q.Bid.Qty <= cap);   // a full bid fill keeps the long within cap
        Assert.True(q.Ask.Qty - inv <= cap);   // a full ask fill keeps the short within cap (i.e. inv-ask >= -cap)
    }

    // ---- Determinism -----------------------------------------------------------------------------

    [Fact]
    public void Quote_is_pure_and_repeatable_including_jitter()
    {
        var a = MarketMakerMath.Quote(100m, false, 7, 100, CurrencyType.USD, Cfg(jitterBps: 3m), aiUserId: 42, stockId: 9);
        var b = MarketMakerMath.Quote(100m, false, 7, 100, CurrencyType.USD, Cfg(jitterBps: 3m), aiUserId: 42, stockId: 9);
        Assert.Equal(a, b);
    }

    [Fact]
    public void No_reference_or_no_cap_yields_no_quote()
    {
        Assert.Equal(default, MarketMakerMath.Quote(0m, false, 0, 100, CurrencyType.USD, Cfg(), 1, 1));
        Assert.Equal(default, MarketMakerMath.Quote(100m, false, 0, 0, CurrencyType.USD, Cfg(), 1, 1));
    }

    // ---- §mood fear-widen (Feature 2) ------------------------------------------------------------

    private static MmConfig WidenCfg(decimal halfBps = 100m, int size = 100, decimal spreadMax = 1.5m,
        decimal sizeMin = 0.6m) =>
        new(Enabled: true, HalfSpreadBps: halfBps, QuoteSize: size, SkewBps: 0m, RequoteThresholdBps: 5m,
            MaxCashFrac: 0.5m, PriceJitterBps: 0m, OneSidedWidenMult: 0m, UseMicro: false,
            MoodWiden: true, MoodWidenSpreadMax: spreadMax, MoodWidenSizeMin: sizeMin);

    [Fact]
    public void Widen_spread_scales_from_one_at_no_fear_to_spreadmax_at_full_fear()
    {
        Assert.Equal(0.01m, MarketMakerMath.MoodWidenSpread(0.01m, fear: 0.0, spreadMax: 1.5m));   // fear 0 ⇒ unchanged
        Assert.Equal(0.015m, MarketMakerMath.MoodWidenSpread(0.01m, fear: 1.0, spreadMax: 1.5m));  // fear 1 ⇒ ×1.5
        Assert.Equal(0.0125m, MarketMakerMath.MoodWidenSpread(0.01m, fear: 0.5, spreadMax: 1.5m)); // halfway
    }

    [Fact]
    public void Widen_size_scales_from_one_at_no_fear_to_sizemin_at_full_fear()
    {
        Assert.Equal(100, MarketMakerMath.MoodWidenSize(100, fear: 0.0, sizeMin: 0.6m));   // fear 0 ⇒ unchanged
        Assert.Equal(60,  MarketMakerMath.MoodWidenSize(100, fear: 1.0, sizeMin: 0.6m));   // fear 1 ⇒ ×0.6
        Assert.Equal(80,  MarketMakerMath.MoodWidenSize(100, fear: 0.5, sizeMin: 0.6m));   // halfway
    }

    [Fact]
    public void Quote_fear_zero_is_byte_identical_to_the_default_overload()
    {
        var off  = MarketMakerMath.Quote(100m, false, 0, 100, CurrencyType.USD, WidenCfg(), 1, 1);
        var zero = MarketMakerMath.Quote(100m, false, 0, 100, CurrencyType.USD, WidenCfg(), 1, 1, fear: 0.0);
        Assert.Equal(off, zero);
    }

    [Fact]
    public void Quote_in_full_fear_widens_the_spread_and_shrinks_the_size()
    {
        var calm = MarketMakerMath.Quote(100m, false, 0, 1000, CurrencyType.USD, WidenCfg(), 1, 1, fear: 0.0);
        var fear = MarketMakerMath.Quote(100m, false, 0, 1000, CurrencyType.USD, WidenCfg(), 1, 1, fear: 1.0);
        // Spread ×1.5: the touch pulls away from the reference on both sides.
        decimal calmSpread = calm.Ask.Price - calm.Bid.Price;
        decimal fearSpread = fear.Ask.Price - fear.Bid.Price;
        Assert.True(fearSpread > calmSpread);
        Assert.Equal(calmSpread * 1.5m, fearSpread);
        // Size ×0.6 (100 → 60), symmetric from flat inventory.
        Assert.Equal(60, fear.Bid.Qty);
        Assert.Equal(60, fear.Ask.Qty);
    }
}
