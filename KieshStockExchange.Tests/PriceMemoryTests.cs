using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// Price-memory anchors §: pure-static tests on the two helpers powering
/// <see cref="BotPriceMemoryService"/> (EwmaStep + ClampToBand), plus a small end-to-end fact
/// driving Tick across a day boundary. Mirrors the pure-static style of EwmaSlopeTests /
/// CashHomeostasisTests.
/// </summary>
public class PriceMemoryTests
{
    private const double Dt = 1.0;

    // EWMA driven for n ticks of constant `price` starting from `start`.
    private static decimal DriveEwma(decimal start, decimal price, int n, double dt, double halfLifeSec)
    {
        var v = start;
        for (int i = 0; i < n; i++)
            v = BotPriceMemoryService.EwmaStep(v, price, dt, halfLifeSec);
        return v;
    }

    [Fact]
    public void EwmaStep_short_halflife_tracks_fast()
    {
        // Pin the halflife semantics directly: 30 ticks @ dt=1 with halflife=30s is exactly one
        // halflife elapsed, so half the gap (100 → 120) should be closed: EWMA ≈ 110.
        var oneHalflife = DriveEwma(100m, 120m, 30, Dt, halfLifeSec: 30.0);
        Assert.InRange(oneHalflife, 109m, 111m);

        // Same drive with a much longer halflife should still be near the start (slow tracking).
        var slow = DriveEwma(100m, 120m, 30, Dt, halfLifeSec: 1800.0);
        Assert.True(slow < 102m, $"slow EWMA should still be near 100, got {slow}");

        // Driven much longer (≥10 halflives), the EWMA converges to the input.
        var converged = DriveEwma(100m, 120m, 600, Dt, halfLifeSec: 30.0);
        Assert.InRange(converged, 119.99m, 120.0001m);
    }

    [Fact]
    public void EwmaStep_flat_input_settles_to_input()
    {
        // Single step at the same price — no movement.
        Assert.Equal(100m, BotPriceMemoryService.EwmaStep(100m, 100m, Dt, 30.0));
        // Driven long at constant input from a constant start — stays put.
        Assert.Equal(100m, DriveEwma(100m, 100m, 600, Dt, 30.0));
    }

    [Fact]
    public void EwmaStep_nonpositive_dt_is_a_no_op()
    {
        Assert.Equal(0.123m, BotPriceMemoryService.EwmaStep(0.123m, 5m, 0.0, 30.0));
        Assert.Equal(0.123m, BotPriceMemoryService.EwmaStep(0.123m, 5m, -1.0, 30.0));
    }

    [Fact]
    public void ClampToBand_hard_clamps_above_and_below()
    {
        Assert.Equal(150m, BotPriceMemoryService.ClampToBand(200m, seed: 100m, maxDrift: 0.5m));
        Assert.Equal(50m,  BotPriceMemoryService.ClampToBand( 30m, seed: 100m, maxDrift: 0.5m));
        // In-band value passes through unchanged.
        Assert.Equal(110m, BotPriceMemoryService.ClampToBand(110m, seed: 100m, maxDrift: 0.5m));
    }

    [Fact]
    public void ClampToBand_with_zero_drift_returns_seed()
    {
        // The escape hatch: maxDrift=0 collapses the band to {seed} — matches today's
        // fixed-seed Fundamental behaviour exactly.
        Assert.Equal(100m, BotPriceMemoryService.ClampToBand(123m, seed: 100m, maxDrift: 0m));
        Assert.Equal(100m, BotPriceMemoryService.ClampToBand( 77m, seed: 100m, maxDrift: 0m));
    }

    [Fact]
    public void ClampToBand_geometric_is_ratio_symmetric_no_down_bias()
    {
        // §log-sym follow-on: geometric [seed/F, seed·F], F=1+d. seed 199, d 0.99 ⇒ F 1.99 ⇒ [100, 396.01].
        // The floor (100) sits FAR above the legacy linear floor (199·0.01 = 1.99) — the down-bias removed — and
        // lo·hi == seed² (ratio-symmetric: the ÷F down-distance equals the ×F up-distance in log-space).
        var lo = BotPriceMemoryService.ClampToBand(1m,   seed: 199m, maxDrift: 0.99m, geometric: true);
        var hi = BotPriceMemoryService.ClampToBand(999m, seed: 199m, maxDrift: 0.99m, geometric: true);
        Assert.Equal(100m, lo);
        Assert.Equal(396.01m, hi);
        Assert.Equal(199m * 199m, lo * hi);
        Assert.True(lo > 199m * (1m - 0.99m), "geometric floor sits above the linear floor (down-bias removed)");
        // Legacy linear overload is untouched (byte-identical default): floor = seed·(1−d) = 1.99.
        Assert.Equal(199m * 0.01m, BotPriceMemoryService.ClampToBand(1m, seed: 199m, maxDrift: 0.99m));
        // The geometric flag threads through AdaptiveAnchorValue (blendWeight 1 ⇒ the clamped fast = the floor).
        Assert.Equal(100m, BotPriceMemoryService.AdaptiveAnchorValue(
            seed: 199m, fast: 1m, blendWeight: 1m, maxTotalExcursion: 0.99m, geometric: true));
    }

    // §weighted-week: linearly-tapered weighted average of the recent N daily TWAPs.
    // Most recent slot weight = WindowDays; oldest slot weight = 1; missing oldest slots route
    // their weight to seed. The whole-cap point of the change is that a single runaway day moves
    // the long anchor by only WindowDays/(WindowDays(WindowDays+1)/2) = 2/(WindowDays+1) of the
    // runaway — not 1.0 like the prior single-snapshot design.

    [Fact]
    public void WeightedAverage_window_1_is_the_most_recent_entry()
    {
        // WindowDays=1 collapses to the prior "previous-day = last TWAP" semantics. With a single
        // entry the weighted average is just that entry; back-compat pinned.
        Assert.Equal(110m, BotPriceMemoryService.WeightedAverage(new[] { 110m }, windowDays: 1, seed: 100m));
        // Even with multiple entries supplied, WindowDays=1 only counts the most recent.
        Assert.Equal(120m, BotPriceMemoryService.WeightedAverage(new[] { 100m, 110m, 120m }, windowDays: 1, seed: 100m));
    }

    [Fact]
    public void WeightedAverage_single_day_runaway_moves_anchor_by_two_over_N_plus_one()
    {
        // With WindowDays=7 and one runaway day TWAP=125 (the rest seed=100), the anchor sits at
        // (7·125 + (6+5+4+3+2+1)·100) / 28 = (875 + 2100) / 28 = 2975/28 ≈ 106.25. That's 25% of
        // the runaway: 2/(7+1) = 1/4. The "1/4 of runaway" intuition the design doc quoted.
        var anchor = BotPriceMemoryService.WeightedAverage(new[] { 125m }, windowDays: 7, seed: 100m);
        Assert.InRange(anchor, 106.24m, 106.26m);
    }

    [Fact]
    public void WeightedAverage_full_window_sustained_runaway_converges_to_runaway()
    {
        // Seven days of the same runaway TWAP → the anchor equals that TWAP. (Σ weight·125)/(Σ
        // weight) = 125. The user's "compounding can happen over a long period" — possible, but
        // requires many sustained days, not one rotation.
        var arr = new[] { 125m, 125m, 125m, 125m, 125m, 125m, 125m };
        Assert.Equal(125m, BotPriceMemoryService.WeightedAverage(arr, windowDays: 7, seed: 100m));
    }

    [Fact]
    public void WeightedAverage_warmup_routes_missing_weight_to_seed()
    {
        // K=2 entries with WindowDays=7. Slots 0 (newest, w=7) and 1 (w=6) carry the 2 entries;
        // slots 2..6 (w=5,4,3,2,1 = 15) route to seed. Total weight 28.
        // (7·130 + 6·120 + 15·100) / 28 = (910 + 720 + 1500) / 28 = 3130/28 ≈ 111.79.
        var anchor = BotPriceMemoryService.WeightedAverage(new[] { 120m, 130m }, windowDays: 7, seed: 100m);
        Assert.InRange(anchor, 111.78m, 111.80m);
    }

    [Fact]
    public void WeightedAverage_empty_history_returns_seed()
    {
        // Day 0 of the session — nothing in the buffer yet. Anchor falls back to seed; byte-identical
        // to the prior pre-rotation behaviour.
        Assert.Equal(100m, BotPriceMemoryService.WeightedAverage(Array.Empty<decimal>(), windowDays: 7, seed: 100m));
    }

    [Fact]
    public void Tick_inert_when_anyConsumer_false()
    {
        // Byte-identical-when-off guarantee at the SERVICE level: with anyConsumer=false, no
        // amount of Ticks moves any getter off the seed.
        var (stocks, _, _) = BuildStocksWithSeeds((1, CurrencyType.USD, 100m));
        var svc = new BotPriceMemoryService(stocks.Object, NullLogger<BotPriceMemoryService>.Instance,
            priceLookup: _ => 200m, anyConsumer: false,
            halfLifeSec: 30.0, dayLengthHours: 0.001 /* tiny so any non-no-op Tick would rotate */);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        svc.Reset(t0);
        for (int i = 1; i <= 100; i++) svc.Tick(t0.AddSeconds(i));
        Assert.Equal(100m, svc.GetRecentEwma(1, CurrencyType.USD));
        Assert.Equal(100m, svc.GetPreviousDayAverage(1, CurrencyType.USD));
    }

    [Fact]
    public void Tick_rotates_day_window_into_previousDayAverage()
    {
        // With anyConsumer=true and a 1-hour day window, after >1 hour of constant price the
        // previous-day-average should equal that constant price (TWAP of a constant series).
        var (stocks, _, _) = BuildStocksWithSeeds((1, CurrencyType.USD, 100m));
        const decimal constantPrice = 110m;
        var svc = new BotPriceMemoryService(stocks.Object, NullLogger<BotPriceMemoryService>.Instance,
            priceLookup: _ => constantPrice, anyConsumer: true,
            halfLifeSec: 30.0, dayLengthHours: 1.0 /* hours */, maxDailyDrift: 0.5m);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        svc.Reset(t0);
        // Tick 1× per second through the hour boundary (3600 ticks puts us at the boundary;
        // one extra tick fires the rotation).
        for (int i = 1; i <= 3601; i++) svc.Tick(t0.AddSeconds(i));
        // Previous-day-average should be the constant price (TWAP of constants is the constant),
        // well within the ±50% clamp band.
        Assert.Equal(constantPrice, svc.GetPreviousDayAverage(1, CurrencyType.USD));
    }

    [Fact]
    public void GetPreviousDayAverage_falls_back_to_seed_during_warmup()
    {
        // Before any rotation, the long-anchor target is the seed — day-0 byte-identical to
        // today's seed-anchor behaviour.
        var (stocks, _, _) = BuildStocksWithSeeds((1, CurrencyType.USD, 100m));
        var svc = new BotPriceMemoryService(stocks.Object, NullLogger<BotPriceMemoryService>.Instance,
            priceLookup: _ => 150m, anyConsumer: true,
            halfLifeSec: 30.0, dayLengthHours: 24.0);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        svc.Reset(t0);
        for (int i = 1; i <= 60; i++) svc.Tick(t0.AddSeconds(i)); // 1 minute — far from rotation
        Assert.Equal(100m, svc.GetPreviousDayAverage(1, CurrencyType.USD));
    }

    // Build a minimal IStockService stub exposing the given (stockId, currency, seedPrice)
    // triples via ById + GetListings — the surface BotPriceMemoryService.Reset uses. Returns the
    // (mock, …) tuple for parity with the test-helper convention; the trailing entries are
    // unused but kept in the signature so tests can assert seed values without re-deriving them.
    private static (Mock<IStockService>, Dictionary<int, Stock>, List<(int, CurrencyType, decimal)>)
        BuildStocksWithSeeds(params (int sid, CurrencyType ccy, decimal seed)[] entries)
    {
        var byId = new Dictionary<int, Stock>();
        var byStock = new Dictionary<int, List<StockListing>>();
        foreach (var e in entries)
        {
            if (!byId.ContainsKey(e.sid)) byId[e.sid] = new Stock { StockId = e.sid };
            if (!byStock.TryGetValue(e.sid, out var list)) byStock[e.sid] = list = new List<StockListing>();
            list.Add(new StockListing { StockId = e.sid, CurrencyType = e.ccy, SeedPrice = e.seed });
        }
        var mock = new Mock<IStockService>(MockBehavior.Loose);
        mock.SetupGet(s => s.ById).Returns(byId);
        mock.Setup(s => s.GetListings(It.IsAny<int>()))
            .Returns<int>(id => byStock.TryGetValue(id, out var list)
                ? (IReadOnlyList<StockListing>)list
                : Array.Empty<StockListing>());
        return (mock, byId, entries.ToList());
    }
}
