using System.Linq;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketEngineServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §bounce lever (a) — determinism + byte-identical-off tests for the bounce-free reference price.
///
///   • <see cref="MidReference.Compute"/> is a pure, all-decimal function: Off / one-sided book /
///     zero-touch-qty ⇒ null (fall back to last-trade); Mid = (bid+ask)/2; Micro = size-weighted.
///   • The matcher captures the reference ONCE on taker arrival and stamps the SAME value on every
///     fill of a multi-level sweep (so a walk-down doesn't drift the reference and re-admit bounce).
///     With the flag off, fills carry a null MidPrice ⇒ byte-identical to today.
///   • <see cref="Candle.ApplyTrade"/> with no MidPrice is byte-identical to last-trade; with a
///     MidPrice it re-anchors Open on the first trade so the Low≤Open≤High invariant holds.
///
/// The static <see cref="MidReference.Mode"/> is process-global, so this suite runs serial (a leaked
/// Mode could perturb a parallel matcher test) and resets to Off in the ctor and Dispose.
/// </summary>
[Collection("MidReferenceSerial")]
public sealed class MidReferenceTests : IDisposable
{
    public MidReferenceTests()
    {
        MidReference.ConfigureForTests(MidRefMode.Off);
        Candle.ContinuousOpenSeed = false;
    }
    public void Dispose()
    {
        MidReference.ConfigureForTests(MidRefMode.Off);
        Candle.ContinuousOpenSeed = false;
    }

    private const int StockId = 42;
    private const CurrencyType Ccy = CurrencyType.USD;

    // ---- Compute: pure function -----------------------------------------------------------------

    [Fact]
    public void Compute_off_returns_null_even_with_two_sided_book()
    {
        MidReference.ConfigureForTests(MidRefMode.Off);
        Assert.Null(MidReference.Compute(50m, 60m, 10, 10));
    }

    [Fact]
    public void Compute_one_sided_or_zero_qty_returns_null()
    {
        MidReference.ConfigureForTests(MidRefMode.Mid);
        Assert.Null(MidReference.Compute(null, 60m, 10, 10));   // no bid
        Assert.Null(MidReference.Compute(50m, null, 10, 10));   // no ask
        Assert.Null(MidReference.Compute(0m, 60m, 10, 10));     // non-positive bid
        MidReference.ConfigureForTests(MidRefMode.Micro);
        Assert.Null(MidReference.Compute(50m, 60m, 0, 0));      // no touch size ⇒ micro divide-by-zero guard
    }

    [Fact]
    public void Compute_mid_is_arithmetic_midpoint_and_pure()
    {
        MidReference.ConfigureForTests(MidRefMode.Mid);
        Assert.Equal(55m, MidReference.Compute(50m, 60m, 7, 3));      // qtys ignored for mid
        Assert.Equal(55m, MidReference.Compute(50m, 60m, 7, 3));      // repeated call identical (pure)
        Assert.Equal(100.125m, MidReference.Compute(100.10m, 100.15m, 1, 1)); // half-cent preserved
    }

    [Fact]
    public void Compute_micro_is_size_weighted_toward_the_larger_opposite_queue()
    {
        MidReference.ConfigureForTests(MidRefMode.Micro);
        // (bid*askQty + ask*bidQty)/(askQty+bidQty) = (50*100 + 60*6)/106 = 5360/106
        Assert.Equal(5360m / 106m, MidReference.Compute(50m, 60m, 6, 100));
    }

    // ---- Matcher: capture once, constant across a multi-level sweep ------------------------------

    private static Order BuyMaker(int orderId, int qty, decimal price) => new()
    {
        OrderId = orderId, UserId = 1, StockId = StockId, CurrencyType = Ccy,
        Side = OrderSide.Buy, Entry = EntryType.Limit, Stop = StopKind.None,
        Quantity = qty, Price = price,
    };

    private static Order SellMaker(int orderId, int qty, decimal price) => new()
    {
        OrderId = orderId, UserId = 3, StockId = StockId, CurrencyType = Ccy,
        Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
        Quantity = qty, Price = price,
    };

    private static Order SellTaker(int orderId, int qty, decimal price) => new()
    {
        OrderId = orderId, UserId = 2, StockId = StockId, CurrencyType = Ccy,
        Side = OrderSide.Sell, Entry = EntryType.Limit, Stop = StopKind.None,
        Quantity = qty, Price = price,
    };

    private static (MatchingEngine matcher, OrderBook book) NewBookWithTouch()
    {
        var matcher = new MatchingEngine(NullLogger<MatchingEngine>.Instance);
        var book = new OrderBook(StockId, Ccy);
        // Two buy levels the sell taker will sweep (best bid 50), plus a resting ask at 60 the taker
        // won't cross — so the book has a two-sided touch and the mid is well-defined.
        book.UpsertOrder(BuyMaker(100, 6, 50m));
        book.UpsertOrder(BuyMaker(101, 4, 49m));
        book.UpsertOrder(SellMaker(300, 100, 60m));
        return (matcher, book);
    }

    [Fact]
    public void Match_mid_stamps_same_pre_sweep_mid_on_every_fill()
    {
        MidReference.ConfigureForTests(MidRefMode.Mid);
        var (matcher, book) = NewBookWithTouch();

        // Sell taker sweeps both buy levels: trade prices zig-zag 50 → 49, but the captured mid is
        // the pre-sweep (bestBid 50 + bestAsk 60)/2 = 55, held constant for both fills.
        var result = matcher.Match(SellTaker(200, 10, 49m), book, CancellationToken.None);

        Assert.Equal(2, result.Fills.Count);
        Assert.Equal(new[] { 50m, 49m }, result.Fills.Select(f => f.Price).ToArray());
        Assert.All(result.Fills, f => Assert.Equal(55m, f.MidPrice));
    }

    [Fact]
    public void Match_off_leaves_midprice_null_byte_identical()
    {
        MidReference.ConfigureForTests(MidRefMode.Off);
        var (matcher, book) = NewBookWithTouch();

        var result = matcher.Match(SellTaker(200, 10, 49m), book, CancellationToken.None);

        Assert.Equal(2, result.Fills.Count);
        Assert.All(result.Fills, f => Assert.Null(f.MidPrice));
    }

    // ---- Candle.ApplyTrade: off byte-identical, on re-anchors Open ------------------------------

    private static Candle PastMinuteCandle(decimal seed)
    {
        var open = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), TimeSpan.FromSeconds(60)) - TimeSpan.FromSeconds(60);
        return new Candle
        {
            StockId = StockId, CurrencyType = Ccy, BucketSeconds = 60, OpenTime = open,
            Open = seed, High = seed, Low = seed, Close = seed, Volume = 0, TradeCount = 0,
        };
    }

    private static Transaction Tick(decimal price, decimal? mid, int txId, int secOffset) => new()
    {
        StockId = StockId, BuyOrderId = 1, SellOrderId = 2, BuyerId = 10, SellerId = 11,
        Quantity = 1, Price = price, MidPrice = mid, CurrencyType = Ccy,
        TransactionId = txId,
        Timestamp = TimeHelper.FloorToBucketUtc(TimeHelper.NowUtc(), TimeSpan.FromSeconds(60))
                    - TimeSpan.FromSeconds(60) + TimeSpan.FromSeconds(secOffset),
    };

    [Fact]
    public void ApplyTrade_without_mid_keys_off_last_trade_price()
    {
        var candle = PastMinuteCandle(seed: 50m);     // seeded at prior close 50
        candle.ApplyTrade(Tick(price: 52m, mid: null, txId: 1, secOffset: 1));
        candle.ApplyTrade(Tick(price: 48m, mid: null, txId: 2, secOffset: 2));

        Assert.Equal(50m, candle.Open);   // seed preserved (legacy branch)
        Assert.Equal(52m, candle.High);
        Assert.Equal(48m, candle.Low);
        Assert.Equal(48m, candle.Close);  // last trade
    }

    [Fact]
    public void ApplyTrade_with_mid_reanchors_open_and_keeps_invariant()
    {
        // Seed (prior last-trade close) sits ABOVE the mid series; without the re-anchor Open>High
        // would throw in IsValid(). The first mid trade re-anchors Open=High=Low to the mid series.
        var candle = PastMinuteCandle(seed: 99m);
        candle.ApplyTrade(Tick(price: 52m, mid: 55m, txId: 1, secOffset: 1));
        candle.ApplyTrade(Tick(price: 48m, mid: 54m, txId: 2, secOffset: 2));

        Assert.Equal(55m, candle.Open);   // re-anchored to first mid, NOT the 99 seed
        Assert.Equal(55m, candle.High);
        Assert.Equal(54m, candle.Low);
        Assert.Equal(54m, candle.Close);  // last mid
        Assert.True(candle.IsValid());    // Low ≤ Open ≤ High holds
    }

    [Fact]
    public void ApplyTrade_with_mid_continuousOpen_keeps_seed_and_stays_continuous()
    {
        // §continuous open: the seeded Open (prior close) is KEPT so open[t]==close[t-1]; the extreme
        // seed-above-mid case still holds the invariant because High/Low envelope the seed.
        Candle.ContinuousOpenSeed = true;
        var candle = PastMinuteCandle(seed: 99m);
        candle.ApplyTrade(Tick(price: 52m, mid: 55m, txId: 1, secOffset: 1));
        candle.ApplyTrade(Tick(price: 48m, mid: 54m, txId: 2, secOffset: 2));

        Assert.Equal(99m, candle.Open);   // KEPT — continuous with the prior bar's close, not the mid
        Assert.Equal(99m, candle.High);   // High envelopes the seed
        Assert.Equal(54m, candle.Low);    // Low tracks the mid series
        Assert.Equal(54m, candle.Close);  // last mid
        Assert.True(candle.IsValid());    // Low ≤ Open ≤ High still holds
    }

    [Fact]
    public void ApplyTrade_with_mid_continuousOpen_normal_seed_within_range()
    {
        // Common case: seed sits inside the mid series ⇒ a clean continuous bar (Open=seed, body to the mids).
        Candle.ContinuousOpenSeed = true;
        var candle = PastMinuteCandle(seed: 54.5m);
        candle.ApplyTrade(Tick(price: 55m, mid: 55m, txId: 1, secOffset: 1));
        candle.ApplyTrade(Tick(price: 54m, mid: 54m, txId: 2, secOffset: 2));

        Assert.Equal(54.5m, candle.Open); // seed kept ⇒ open[t]==close[t-1]
        Assert.Equal(55m, candle.High);
        Assert.Equal(54m, candle.Low);
        Assert.Equal(54m, candle.Close);
        Assert.True(candle.IsValid());
    }
}

/// <summary>Serial collection so the process-global <see cref="MidReference.Mode"/> can't leak across parallel tests.</summary>
[CollectionDefinition("MidReferenceSerial", DisableParallelization = true)]
public sealed class MidReferenceSerialCollection { }
