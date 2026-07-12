using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §filtered-tape H/L (Candles:HLMinFillSize) — the SIP odd-lot / TradingView rule: fills below the
/// threshold count toward volume/close/vwap but do NOT set candle High/Low; H/L = extremes over
/// (eligible fills ∪ {Open, Close}), the O/C envelope keeping every candle well-formed.
///
/// Candle.HLMinFillSize is process-global (set at startup like Candle.VwapClose), so this suite
/// shares the serial collection with MidReferenceTests and resets to 0 in the ctor and Dispose.
/// </summary>
[Collection("MidReferenceSerial")]
public sealed class HLMinFillSizeTests : IDisposable
{
    public HLMinFillSizeTests()
    {
        MidReference.ConfigureForTests(MidRefMode.Off);
        Candle.HLMinFillSize = 0;
    }
    public void Dispose()
    {
        MidReference.ConfigureForTests(MidRefMode.Off);
        Candle.HLMinFillSize = 0;
    }

    private const int StockId = 42;
    private const CurrencyType Ccy = CurrencyType.USD;

    private static Candle PastMinuteCandle(decimal seed)
    {
        var open = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), TimeSpan.FromSeconds(60)) - TimeSpan.FromSeconds(60);
        return new Candle
        {
            StockId = StockId, CurrencyType = Ccy, BucketSeconds = 60, OpenTime = open,
            Open = seed, High = seed, Low = seed, Close = seed, Volume = 0, TradeCount = 0,
        };
    }

    private static Transaction Tick(decimal price, int qty, int txId, int secOffset) => new()
    {
        StockId = StockId, BuyOrderId = 1, SellOrderId = 2, BuyerId = 10, SellerId = 11,
        Quantity = qty, Price = price, MidPrice = null, CurrencyType = Ccy,
        TransactionId = txId,
        Timestamp = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), TimeSpan.FromSeconds(60))
                    - TimeSpan.FromSeconds(60) + TimeSpan.FromSeconds(secOffset),
    };

    [Fact]
    public void Off_is_byte_identical_every_fill_sets_HL()
    {
        Candle.HLMinFillSize = 0;
        var c = PastMinuteCandle(seed: 50m);
        c.ApplyTrade(Tick(price: 55m, qty: 1, txId: 1, secOffset: 1));   // tiny fill
        c.ApplyTrade(Tick(price: 45m, qty: 1, txId: 2, secOffset: 2));   // tiny fill
        c.ApplyTrade(Tick(price: 50m, qty: 100, txId: 3, secOffset: 3));
        Assert.Equal(55m, c.High);
        Assert.Equal(45m, c.Low);
        Assert.Equal(50m, c.Close);
    }

    [Fact]
    public void Small_fills_do_not_extend_HL_but_count_volume_and_trades()
    {
        Candle.HLMinFillSize = 10;
        var c = PastMinuteCandle(seed: 50m);
        c.ApplyTrade(Tick(price: 51m, qty: 100, txId: 1, secOffset: 1)); // eligible → High 51
        c.ApplyTrade(Tick(price: 56m, qty: 3, txId: 2, secOffset: 2));   // tiny sweep print, then price returns
        c.ApplyTrade(Tick(price: 50m, qty: 100, txId: 3, secOffset: 3)); // eligible close back at 50
        Assert.Equal(51m, c.High);          // the 56 print never set the High
        Assert.Equal(50m, c.Low);           // seed/O-C envelope
        Assert.Equal(50m, c.Close);
        Assert.Equal(203, c.Volume);        // tiny fill still counted
        Assert.Equal(3, c.TradeCount);
    }

    [Fact]
    public void Close_envelope_keeps_candle_valid_when_last_fill_is_tiny()
    {
        Candle.HLMinFillSize = 10;
        var c = PastMinuteCandle(seed: 50m);
        c.ApplyTrade(Tick(price: 50m, qty: 100, txId: 1, secOffset: 1));
        // The minute ENDS on a tiny fill above the filtered range: it can't extend the wick on its
        // own, but Close must stay inside [Low, High] → the envelope pulls High up to the Close.
        c.ApplyTrade(Tick(price: 53m, qty: 2, txId: 2, secOffset: 2));
        Assert.Equal(53m, c.Close);
        Assert.Equal(53m, c.High);          // envelope, not the raw-wick update
        Assert.True(c.IsValid());
    }

    [Fact]
    public void All_ineligible_minute_degrades_to_Open_Close_envelope()
    {
        Candle.HLMinFillSize = 10;
        var c = PastMinuteCandle(seed: 50m);
        c.ApplyTrade(Tick(price: 57m, qty: 1, txId: 1, secOffset: 1));
        c.ApplyTrade(Tick(price: 44m, qty: 2, txId: 2, secOffset: 2));
        c.ApplyTrade(Tick(price: 49m, qty: 3, txId: 3, secOffset: 3));
        // No fill was H/L-eligible: intermediate tiny prints held the wick only WHILE they were the
        // live close (57 then 44 released as price moved on) — the final bar is the {Open, Close}
        // envelope, the odd-lot-only-minute analog (real tapes record no official range update).
        Assert.Equal(50m, c.High);          // Open
        Assert.Equal(49m, c.Low);           // final Close
        Assert.Equal(49m, c.Close);
        Assert.True(c.IsValid());
    }

    [Fact]
    public void Vwap_close_mode_composes_with_the_filter()
    {
        MidReference.ConfigureForTests(MidRefMode.Vwap);
        Candle.HLMinFillSize = 10;
        var c = PastMinuteCandle(seed: 50m);
        c.ApplyTrade(Tick(price: 50m, qty: 90, txId: 1, secOffset: 1));
        c.ApplyTrade(Tick(price: 58m, qty: 2, txId: 2, secOffset: 2));   // tiny print far above
        // vwap close = (50*90 + 58*2)/92 ≈ 50.17 — inside range; the 58 never set the High.
        Assert.Equal((50m * 90 + 58m * 2) / 92m, c.Close);
        Assert.True(c.High < 58m);
        Assert.True(c.Close <= c.High && c.Close >= c.Low);
        Assert.True(c.IsValid());
    }

    [Fact]
    public void Exact_threshold_is_eligible()
    {
        Candle.HLMinFillSize = 10;
        var c = PastMinuteCandle(seed: 50m);
        c.ApplyTrade(Tick(price: 54m, qty: 10, txId: 1, secOffset: 1));  // == threshold → eligible
        Assert.Equal(54m, c.High);
    }
}
