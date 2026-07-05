using System.Linq;
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
        public int GlobalSign;  // §global co-fire: the shared-pulse sign the test controls between ticks.
        public void Reset() { }
        public IEnumerable<ShockImpulse> Poll(long simTick, double dt) => Emit;
        public int LastGlobalSign => GlobalSign;
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

    // ---- §global-exog: shared market-wide shock impulses (cross-stock correlation lever) ----

    private static readonly int[] AllIds = { 1, 2, 3 };

    // A market-wide impulse = one signed magnitude present for EVERY stock the same tick (all equal); per-stock
    // impulses are independent draws so they (essentially) never collide across all stocks. This detects it.
    private static bool HasMarketWideImpulse(IEnumerable<ShockImpulse> imps)
        => imps.GroupBy(i => i.SignedMagnitude)
               .Any(g => g.Select(i => i.StockId).Distinct().Count() == AllIds.Length);

    [Fact]
    public void GlobalFraction_zero_never_fires_a_market_wide_impulse()
    {
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 25m)).Object;
        var src = new RandomShockSource(stocks, meanIntervalMinutes: 0.2, minMagnitude: 0.01,
            maxMagnitude: 0.06, magnitudeExponent: 1.8, globalFraction: 0.0);
        src.Reset();
        for (int t = 1; t <= 3000; t++)
            Assert.False(HasMarketWideImpulse(src.Poll(t, 1.0))); // per-stock only ⇒ no all-stocks shared magnitude
    }

    [Fact]
    public void GlobalFraction_one_fires_the_same_magnitude_to_every_stock()
    {
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 25m)).Object;
        var src = new RandomShockSource(stocks, meanIntervalMinutes: 0.2, minMagnitude: 0.01,
            maxMagnitude: 0.06, magnitudeExponent: 1.8, globalFraction: 1.0);
        src.Reset();
        bool sawGlobal = false;
        for (int t = 1; t <= 3000 && !sawGlobal; t++)
        {
            var shared = src.Poll(t, 1.0).GroupBy(i => i.SignedMagnitude)
                            .FirstOrDefault(g => g.Select(i => i.StockId).Distinct().Count() == AllIds.Length);
            if (shared != null)
            {
                sawGlobal = true;
                Assert.All(shared, i => Assert.Equal(shared.Key, i.SignedMagnitude)); // identical across all stocks
            }
        }
        Assert.True(sawGlobal, "GlobalFraction=1 should fire a market-wide impulse within 3000 ticks.");
    }

    [Fact]
    public void GlobalFraction_default_is_zero_and_per_stock_stream_is_deterministic()
    {
        // Default ctor arg (no globalFraction) ⇒ per-stock-only; two same-seed sources emit identical sequences
        // (the dedicated global RNG is never drawn at 0 ⇒ the per-stock RNG stream is untouched = byte-identical).
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 25m)).Object;
        var a = new RandomShockSource(stocks, 0.2, 0.01, 0.06, 1.8);
        var b = new RandomShockSource(stocks, 0.2, 0.01, 0.06, 1.8);
        a.Reset(); b.Reset();
        for (int t = 1; t <= 1500; t++)
        {
            var ia = a.Poll(t, 1.0).ToList();
            var ib = b.Poll(t, 1.0).ToList();
            Assert.False(HasMarketWideImpulse(ia));
            Assert.Equal(ia.Count, ib.Count);
            for (int k = 0; k < ia.Count; k++)
            {
                Assert.Equal(ia[k].StockId, ib[k].StockId);
                Assert.Equal(ia[k].SignedMagnitude, ib[k].SignedMagnitude);
            }
        }
    }

    // ---- §global co-fire: the global-pulse signal that drives the same-tick, same-sign taker burst ----

    [Fact]
    public void Source_LastGlobalSign_is_zero_when_globalFraction_zero()
    {
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 25m)).Object;
        var src = new RandomShockSource(stocks, 0.2, 0.01, 0.06, 1.8, globalFraction: 0.0);
        src.Reset();
        for (int t = 1; t <= 3000; t++) { src.Poll(t, 1.0).ToList(); Assert.Equal(0, src.LastGlobalSign); }
    }

    [Fact]
    public void Source_LastGlobalSign_matches_the_shared_impulse_sign()
    {
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 25m)).Object;
        var src = new RandomShockSource(stocks, 0.2, 0.01, 0.06, 1.8, globalFraction: 1.0);
        src.Reset();
        bool sawGlobal = false;
        for (int t = 1; t <= 3000 && !sawGlobal; t++)
        {
            var imps   = src.Poll(t, 1.0).ToList();
            var shared = imps.GroupBy(i => i.SignedMagnitude)
                             .FirstOrDefault(g => g.Select(i => i.StockId).Distinct().Count() == AllIds.Length);
            if (shared != null)
            {
                sawGlobal = true;
                Assert.Equal(Math.Sign(shared.Key), src.LastGlobalSign); // sign of the shared magnitude
                Assert.NotEqual(0, src.LastGlobalSign);
            }
            else Assert.Equal(0, src.LastGlobalSign); // no market-wide impulse this tick ⇒ signal is 0
        }
        Assert.True(sawGlobal, "GlobalFraction=1 should fire a market-wide impulse within 3000 ticks.");
    }

    [Fact]
    public void Service_relays_global_sign_and_bumps_pulse_id_on_a_pulse()
    {
        var src = new StepSource();
        var svc = Build(src);
        svc.Reset(T0);

        src.GlobalSign = 0; src.Emit = new List<ShockImpulse>();          // no pulse
        svc.Tick(T0.AddSeconds(1));
        Assert.Equal(0, svc.GlobalCoFireSign);
        Assert.Equal(0, svc.GlobalPulseId);

        src.GlobalSign = -1;                                              // a market-wide DOWN pulse
        src.Emit = new List<ShockImpulse> { new ShockImpulse(1, -0.05), new ShockImpulse(2, -0.05) };
        svc.Tick(T0.AddSeconds(2));
        Assert.Equal(-1, svc.GlobalCoFireSign);
        Assert.Equal(1, svc.GlobalPulseId);

        src.GlobalSign = 0; src.Emit = new List<ShockImpulse>();          // pulse over ⇒ sign clears, id holds
        svc.Tick(T0.AddSeconds(3));
        Assert.Equal(0, svc.GlobalCoFireSign);
        Assert.Equal(1, svc.GlobalPulseId);
    }

    [Fact]
    public void Service_disabled_never_reports_a_co_fire_sign()
    {
        var src = new StepSource { GlobalSign = 1, Emit = { new ShockImpulse(1, 0.05) } };
        var svc = Build(src, enabled: false);
        svc.Reset(T0);
        Drive(svc, 10);
        Assert.Equal(0, svc.GlobalCoFireSign);
        Assert.Equal(0, svc.GlobalPulseId);
    }
}
