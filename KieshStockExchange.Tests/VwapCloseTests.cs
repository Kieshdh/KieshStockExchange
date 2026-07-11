using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using KieshStockExchange.Services.MarketEngineServices;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §bounce vwap — the candle CLOSE becomes the per-bucket VWAP (Σ price·qty / Σ qty of the raw
/// trade tape) when Bots:BounceReference = "vwap". Pure sampling change: O/H/L stay on the raw
/// tape (so the convex-combination close is always inside [Low, High]), trades keep their mid
/// stamp, and the flag off is byte-identical to today.
///
/// Candle.VwapClose is process-global (mirrored from MidReference.Mode), so this suite shares the
/// serial collection with MidReferenceTests and resets to Off in the ctor and Dispose.
/// </summary>
[Collection("MidReferenceSerial")]
public sealed class VwapCloseTests : IDisposable
{
    public VwapCloseTests() => MidReference.ConfigureForTests(MidRefMode.Off);
    public void Dispose() => MidReference.ConfigureForTests(MidRefMode.Off);

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

    private static Transaction Tick(decimal price, int qty, decimal? mid, int txId, int secOffset) => new()
    {
        StockId = StockId, BuyOrderId = 1, SellOrderId = 2, BuyerId = 10, SellerId = 11,
        Quantity = qty, Price = price, MidPrice = mid, CurrencyType = Ccy,
        TransactionId = txId,
        Timestamp = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), TimeSpan.FromSeconds(60))
                    - TimeSpan.FromSeconds(60) + TimeSpan.FromSeconds(secOffset),
    };

    // ---- Mode wiring ----------------------------------------------------------------------------

    [Fact]
    public void Vwap_mode_sets_candle_flag_and_computes_like_mid()
    {
        MidReference.ConfigureForTests(MidRefMode.Vwap);
        Assert.True(MidReference.VwapClose);
        Assert.True(Candle.VwapClose);
        // Trades keep the plain mid stamp in vwap mode — the tape data stays useful.
        Assert.Equal(55m, MidReference.Compute(50m, 60m, 7, 3));

        MidReference.ConfigureForTests(MidRefMode.Off);
        Assert.False(MidReference.VwapClose);
        Assert.False(Candle.VwapClose);
    }

    // ---- ApplyTrade: off byte-identical, on = running VWAP ---------------------------------------

    [Fact]
    public void ApplyTrade_flag_off_is_byte_identical_to_today()
    {
        // Close keys off MidPrice ?? Price, exactly as before this lever existed.
        var candle = PastMinuteCandle(seed: 50m);
        candle.ApplyTrade(Tick(price: 52m, qty: 1, mid: null, txId: 1, secOffset: 1));
        candle.ApplyTrade(Tick(price: 48m, qty: 1, mid: 49m, txId: 2, secOffset: 2));

        Assert.Equal(50m, candle.Open);
        Assert.Equal(52m, candle.High);
        Assert.Equal(49m, candle.Low);    // mid series drives H/L in the legacy branch
        Assert.Equal(49m, candle.Close);  // MidPrice ?? Price
    }

    [Fact]
    public void ApplyTrade_vwap_close_is_quantity_weighted_and_inside_range()
    {
        MidReference.ConfigureForTests(MidRefMode.Vwap);
        var candle = PastMinuteCandle(seed: 50m);
        // Mid stamps deliberately far off — vwap must key off the RAW tape, not the mid.
        candle.ApplyTrade(Tick(price: 52m, qty: 3, mid: 99m, txId: 1, secOffset: 1));
        candle.ApplyTrade(Tick(price: 48m, qty: 1, mid: 99m, txId: 2, secOffset: 2));

        Assert.Equal(50m, candle.Open);   // seed preserved (legacy semantics, no mid re-anchor)
        Assert.Equal(52m, candle.High);   // raw tape, not the 99 mid
        Assert.Equal(48m, candle.Low);
        Assert.Equal(51m, candle.Close);  // (52*3 + 48*1) / 4
        Assert.InRange(candle.Close, candle.Low, candle.High);
        Assert.Equal(4L, candle.Volume);
        Assert.True(candle.IsValid());
    }

    [Fact]
    public void ApplyTrade_vwap_running_close_updates_per_trade()
    {
        MidReference.ConfigureForTests(MidRefMode.Vwap);
        var candle = PastMinuteCandle(seed: 10m);
        candle.ApplyTrade(Tick(price: 10m, qty: 1, mid: null, txId: 1, secOffset: 1));
        Assert.Equal(10m, candle.Close);
        candle.ApplyTrade(Tick(price: 20m, qty: 3, mid: null, txId: 2, secOffset: 2));
        Assert.Equal(17.5m, candle.Close); // (10*1 + 20*3) / 4
    }

    [Fact]
    public void Clone_preserves_the_running_vwap_accumulator()
    {
        MidReference.ConfigureForTests(MidRefMode.Vwap);
        var candle = PastMinuteCandle(seed: 10m);
        candle.ApplyTrade(Tick(price: 10m, qty: 1, mid: null, txId: 1, secOffset: 1));

        var clone = candle.Clone();
        clone.ApplyTrade(Tick(price: 20m, qty: 3, mid: null, txId: 2, secOffset: 2));
        Assert.Equal(17.5m, clone.Close); // notional survived the clone: (10 + 60) / 4
    }

    // ---- Higher-timeframe aggregation: volume-weighted child closes ------------------------------

    private static Candle Child(decimal close, long volume) => new()
    {
        StockId = StockId, CurrencyType = Ccy, BucketSeconds = 60,
        Open = close, High = close, Low = close, Close = close,
        Volume = volume, TradeCount = volume > 0 ? 1 : 0,
    };

    [Fact]
    public void WeightedClose_is_volume_weighted_mean_of_child_closes()
    {
        // Child closes are per-bucket VWAPs ⇒ their volume-weighted mean is the true bucket VWAP.
        var children = new[] { Child(10m, 1), Child(20m, 3), Child(15m, 0) }; // zero-vol child = weight 0
        Assert.Equal(17.5m, CandleService.WeightedClose(children));           // (10*1 + 20*3) / 4
    }

    [Fact]
    public void WeightedClose_zero_total_volume_falls_back_to_last_close()
    {
        var children = new[] { Child(10m, 0), Child(12m, 0) }; // gap-filled flats
        Assert.Equal(12m, CandleService.WeightedClose(children));
    }
}
