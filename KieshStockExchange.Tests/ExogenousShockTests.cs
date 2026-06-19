using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §exogenous-information: end-to-end tests for the <see cref="ExogenousShockService"/> state machine and the
/// anchor-tracks-shock composition in <see cref="FundamentalService"/>. Determinism uses a synthetic Tick
/// schedule (NOT realized price), and the accumulator/hysteresis cases use a scripted <see cref="IShockSource"/>
/// so the bounded-walk + reshuffle logic is checked deterministically.
/// </summary>
public class ExogenousShockTests
{
    private const CurrencyType Usd = CurrencyType.USD;
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Scripted source whose emitted impulses the test controls between ticks.
    private sealed class StepSource : IShockSource
    {
        public List<ShockImpulse> Emit = new();
        public void Reset() { }
        public IEnumerable<ShockImpulse> Poll(long simTick, double dt) => Emit;
    }

    private static Mock<IStockService> BuildStocks(params (int sid, decimal seed)[] entries)
    {
        var byId = new Dictionary<int, Stock>();
        var byStock = new Dictionary<int, List<StockListing>>();
        foreach (var e in entries)
        {
            byId[e.sid] = new Stock { StockId = e.sid };
            byStock[e.sid] = new List<StockListing>
                { new StockListing { StockId = e.sid, CurrencyType = Usd, SeedPrice = e.seed } };
        }
        var mock = new Mock<IStockService>(MockBehavior.Loose);
        mock.SetupGet(s => s.ById).Returns(byId);
        mock.Setup(s => s.GetListings(It.IsAny<int>()))
            .Returns<int>(id => byStock.TryGetValue(id, out var l) ? (IReadOnlyList<StockListing>)l : Array.Empty<StockListing>());
        return mock;
    }

    private static ExogenousShockService Build(IShockSource source, bool enabled = true,
        double cap = 0.06, double halfLife = 300.0, double floor = 0.001)
        => new(BuildStocks((1, 100m), (2, 50m)).Object, new StockProfileService(enabled: false),
               NullLogger<ExogenousShockService>.Instance, source,
               enabled: enabled, decayHalfLifeSec: halfLife, cap: cap, floor: floor, softWallK: 0.1);

    private static void Drive(ExogenousShockService svc, int ticks, double dtSec = 1.0)
    {
        for (int i = 1; i <= ticks; i++) svc.Tick(T0.AddSeconds(i * dtSec));
    }

    [Fact]
    public void Disabled_returns_zero_and_tick_is_noop()
    {
        var src = new StepSource { Emit = { new ShockImpulse(1, 0.05) } };
        var svc = Build(src, enabled: false);
        svc.Reset(T0);
        Drive(svc, 20);
        Assert.Equal(0.0, svc.GetShock(1));
        Assert.Equal(0, svc.GetShockId(1));
        Assert.False(svc.AnyActive);
    }

    [Fact]
    public void Inert_until_armed()
    {
        var src = new StepSource { Emit = { new ShockImpulse(1, 0.05) } };
        var svc = Build(src);
        Assert.Equal(0.0, svc.GetShock(1));   // not reset yet
        svc.Reset(T0);
        Assert.Equal(0.0, svc.GetShock(1));   // armed but no Tick yet
        Assert.False(svc.AnyActive);
    }

    [Fact]
    public void Accumulator_is_bounded_by_cap()
    {
        var src = new StepSource { Emit = { new ShockImpulse(1, 0.05) } }; // same-sign top-ups every tick
        var svc = Build(src, cap: 0.06);
        svc.Reset(T0);
        Drive(svc, 200);
        var v = svc.GetShock(1);
        Assert.InRange(v, 0.0, 0.06 + 1e-9); // never exceeds +cap despite repeated +0.05 arrivals
        Assert.True(v > 0.0);
    }

    [Fact]
    public void ShockId_stable_across_topups_of_a_live_shock()
    {
        var src = new StepSource { Emit = { new ShockImpulse(1, 0.02) } };
        var svc = Build(src);
        svc.Reset(T0);
        Drive(svc, 50);
        Assert.Equal(1, svc.GetShockId(1)); // one new-from-rest arrival, then stable top-ups
        Assert.True(svc.AnyActive);
    }

    [Fact]
    public void ShockId_increments_on_a_new_impulse_from_rest()
    {
        var src = new StepSource();
        var svc = Build(src, halfLife: 5.0, floor: 0.001);
        svc.Reset(T0);

        src.Emit = new List<ShockImpulse> { new ShockImpulse(1, 0.05) };
        svc.Tick(T0.AddSeconds(1));
        Assert.Equal(1, svc.GetShockId(1));

        // Stop arrivals and let it decay below the floor with a large dt step.
        src.Emit = new List<ShockImpulse>();
        svc.Tick(T0.AddSeconds(61)); // ~12 half-lives ⇒ shock ≈ 0 < floor ⇒ dropped
        Assert.Equal(0.0, svc.GetShock(1));

        // A fresh impulse from rest bumps the id again (cohort reshuffles).
        src.Emit = new List<ShockImpulse> { new ShockImpulse(1, 0.05) };
        svc.Tick(T0.AddSeconds(62));
        Assert.Equal(2, svc.GetShockId(1));
    }

    [Fact]
    public void Two_instances_with_same_seed_produce_identical_series()
    {
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 25m)).Object;
        ExogenousShockService Make() => new(stocks, new StockProfileService(enabled: false),
            NullLogger<ExogenousShockService>.Instance,
            new RandomShockSource(stocks, meanIntervalMinutes: 0.5, minMagnitude: 0.01, maxMagnitude: 0.06,
                magnitudeExponent: 1.8),
            enabled: true, decayHalfLifeSec: 120.0, cap: 0.06);

        var a = Make(); var b = Make();
        a.Reset(T0); b.Reset(T0);
        for (int i = 1; i <= 400; i++)
        {
            a.Tick(T0.AddSeconds(i)); b.Tick(T0.AddSeconds(i));
            foreach (var sid in new[] { 1, 2, 3 })
            {
                Assert.Equal(a.GetShock(sid), b.GetShock(sid));
                Assert.Equal(a.GetShockId(sid), b.GetShockId(sid));
            }
        }
    }

    // ---- Anchor-tracks-shock composition in FundamentalService ----

    private static FundamentalService BuildFund(Func<int, double> shock, Func<bool> anyActive)
        => new(BuildStocks((1, 100m)).Object, new StockProfileService(enabled: false),
               NullLogger<FundamentalService>.Instance,
               enabled: true, band: 0.12m, theta: 0.02, sigma: 0.0, driftIntervalSec: 60.0,
               exogShock: shock, anyShockActive: anyActive, shockCap: 0.06m);

    [Fact]
    public void Anchor_is_byte_identical_when_shock_idle()
    {
        // exogShock wired but reads 0 / global says none active ⇒ Get == seed (legacy), no round-trip drift.
        var idleZero = BuildFund(_ => 0.0, () => true);
        idleZero.Reset();
        Assert.Equal(100m, idleZero.Get(1, Usd));

        var fastPathOff = BuildFund(_ => 0.05, () => false); // anyActive=false ⇒ skip composition
        fastPathOff.Reset();
        Assert.Equal(100m, fastPathOff.Get(1, Usd));
    }

    [Fact]
    public void Anchor_tracks_shock_and_stays_within_band_plus_cap()
    {
        var moved = BuildFund(_ => 0.05, () => true);
        moved.Reset();
        Assert.Equal(105m, moved.Get(1, Usd)); // seed 100 × (1 + 0.05)

        // A huge shock is clamped to seed × (1 + (Band + Cap)) = 100 × 1.18.
        var clamped = BuildFund(_ => 5.0, () => true);
        clamped.Reset();
        Assert.Equal(118m, clamped.Get(1, Usd));
    }
}
