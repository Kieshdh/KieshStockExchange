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
