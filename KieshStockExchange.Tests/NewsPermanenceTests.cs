using System.Linq;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using KieshStockExchange.Services.DataServices.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KieshStockExchange.Tests;

/// <summary>
/// §news-permanence (spec docs/NEWS_PERMANENCE_COUPLING.md): proof obligations for the variable-permanence,
/// decay-coupled news event system. Covers byte-identical-off, the α↔τ NEGATIVE coupling, decay-to-raised-base,
/// the geometric-band clamp on the accumulated residual, aftershock scheduling (MaxDepth 1), and the
/// transient/residual split exposure (GetTransient vs GetShock).
/// </summary>
public class NewsPermanenceTests
{
    private const CurrencyType Usd = CurrencyType.USD;
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Scripted source whose emitted impulses (incl. α/τ) the test controls between ticks.
    private sealed class StepSource : IShockSource
    {
        public List<ShockImpulse> Emit = new();
        public void Reset() { }
        public IEnumerable<ShockImpulse> Poll(long simTick, double dt) => Emit;
        public int LastGlobalSign => 0;
        public int LastGlobalSector => -1;
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

    private static ExogenousShockService BuildSvc(IShockSource source, bool enabled = true,
        double cap = 0.06, double halfLife = 300.0, double floor = 0.001, double residualHalfLife = 10800.0)
        => new(BuildStocks((1, 100m), (2, 50m)).Object, new StockProfileService(enabled: false),
               NullLogger<ExogenousShockService>.Instance, source,
               enabled: enabled, decayHalfLifeSec: halfLife, cap: cap, floor: floor, softWallK: 0.1,
               residualHalfLifeSec: residualHalfLife);

    // ---- (a) byte-identical when off ----

    [Fact]
    public void Permanence_off_source_stream_is_identical_to_legacy_and_carries_the_sentinel()
    {
        var stocks = BuildStocks((1, 100m), (2, 50m), (3, 25m)).Object;
        var legacy = new RandomShockSource(stocks, 0.3, 0.01, 0.06, 1.8, globalFraction: 0.2);
        var offPerm = new RandomShockSource(stocks, 0.3, 0.01, 0.06, 1.8, globalFraction: 0.2,
            permanence: new NewsPermanenceOptions { Enabled = false });
        legacy.Reset(); offPerm.Reset();
        for (int t = 1; t <= 3000; t++)
        {
            var a = legacy.Poll(t, 1.0).ToList();
            var b = offPerm.Poll(t, 1.0).ToList();
            Assert.Equal(a.Count, b.Count);
            for (int k = 0; k < a.Count; k++)
            {
                Assert.Equal(a[k].StockId, b[k].StockId);
                Assert.Equal(a[k].SignedMagnitude, b[k].SignedMagnitude);
                Assert.Equal(0.0, b[k].PermanentFraction); // α=0 sentinel
                Assert.Equal(0.0, b[k].DecayHalfLifeSec);   // τ=0 sentinel
            }
        }
        Assert.Equal(0, offPerm.BaseEventCount);   // no permanence bookkeeping when off
        Assert.Equal(0, offPerm.AftershockCount);
    }

    [Fact]
    public void Service_off_impulses_decay_to_zero_with_no_residual()
    {
        // A sentinel impulse (α=0/τ=0) behaves exactly like the pre-permanence decay-to-zero accumulator.
        var src = new StepSource { Emit = { new ShockImpulse(1, 0.05) } };
        var svc = BuildSvc(src, halfLife: 5.0);
        svc.Reset(T0);
        svc.Tick(T0.AddSeconds(1));
        Assert.Equal(svc.GetShock(1), svc.GetTransient(1)); // residual is 0 ⇒ transient == total
        Assert.True(svc.GetShock(1) > 0.0);

        src.Emit = new List<ShockImpulse>();          // stop arrivals; decay many half-lives
        for (int i = 2; i <= 40; i++) svc.Tick(T0.AddSeconds(i * 5));
        Assert.Equal(0.0, svc.GetShock(1));           // dropped below floor ⇒ back to 0 (no residual floor)
        Assert.False(svc.AnyActive);
    }

    // ---- (b) α↔τ NEGATIVE coupling ----

    private static readonly NewsPermanenceOptions Base = new();

    [Fact]
    public void High_z_gives_high_alpha_and_short_tau_low_z_the_opposite()
    {
        // Deterministic latent (jitters 0): +z ⇒ high α + short τ; −z ⇒ low α + long τ (the negative coupling).
        var (aHi, tHi) = RandomShockSource.ComputeAlphaTau(2.0, 0.0, 0.0, Base, 0.0, 1.0);
        var (aLo, tLo) = RandomShockSource.ComputeAlphaTau(-2.0, 0.0, 0.0, Base, 0.0, 1.0);
        Assert.True(aHi > aLo, $"α(+z) {aHi} should exceed α(−z) {aLo}");
        Assert.True(tHi < tLo, $"τ(+z) {tHi} should be shorter than τ(−z) {tLo}");
        Assert.InRange(aHi, Base.AlphaMin, Base.AlphaMax);
        Assert.InRange(tLo, Base.TauMinSec, Base.TauMaxSec);
    }

    [Fact]
    public void Coupling_produces_negative_correlation_and_zero_coupling_is_independent()
    {
        Assert.True(CorrAlphaLnTau(new NewsPermanenceOptions { Coupling = 0.6 }) < -0.45,
            "Coupling=0.6 must yield a strongly NEGATIVE corr(α, ln τ).");
        Assert.True(Math.Abs(CorrAlphaLnTau(new NewsPermanenceOptions { Coupling = 0.0 })) < 0.06,
            "Coupling=0 must decorrelate α and τ.");
    }

    private static double CorrAlphaLnTau(NewsPermanenceOptions o)
    {
        var rng = new Random(12345);
        int n = 20000;
        double sa = 0, st = 0;
        var a = new double[n]; var lt = new double[n];
        for (int i = 0; i < n; i++)
        {
            var (alpha, tau) = RandomShockSource.DrawAlphaTau(rng, o, 0.0, 1.0);
            a[i] = alpha; lt[i] = Math.Log(tau); sa += alpha; st += lt[i];
        }
        double ma = sa / n, mt = st / n, cov = 0, va = 0, vt = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - ma, dt = lt[i] - mt;
            cov += da * dt; va += da * da; vt += dt * dt;
        }
        return cov / Math.Sqrt(va * vt);
    }

    // ---- (c)/(f) decay to the RAISED base α·M + transient/residual split ----

    [Fact]
    public void Decays_to_the_raised_base_and_the_transient_fades_while_the_residual_persists()
    {
        // One impulse M=+0.03, α=0.6, τ=10s; residual half-life huge so the floor holds. Softwall is ~0 near the
        // middle, so the applied step == M ⇒ residual = 0.6·0.03 = 0.018, transient = 0.4·0.03 = 0.012.
        const double m = 0.03, alpha = 0.6;
        var src = new StepSource { Emit = { new ShockImpulse(1, m, alpha, 10.0) } };
        var svc = BuildSvc(src, cap: 0.06, residualHalfLife: 1e9);
        svc.Reset(T0);
        svc.Tick(T0.AddSeconds(1));

        double raisedBase = alpha * m; // 0.018
        Assert.Equal(m, svc.GetShock(1), 6);                 // full pop at onset
        Assert.Equal((1 - alpha) * m, svc.GetTransient(1), 6); // transient = (1−α)M

        src.Emit = new List<ShockImpulse>();                 // stop arrivals; let the transient bleed off
        double prevTransient = svc.GetTransient(1);
        for (int i = 2; i <= 120; i++)
        {
            svc.Tick(T0.AddSeconds(i));
            double tr = svc.GetTransient(1);
            Assert.True(tr <= prevTransient + 1e-12, "transient must be monotone non-increasing");
            Assert.True(svc.GetShock(1) >= raisedBase - 1e-9, "GetShock must never fall below the raised base α·M");
            prevTransient = tr;
        }
        Assert.True(svc.GetTransient(1) < 1e-4, "transient should have essentially vanished after ~11 half-lives");
        Assert.Equal(raisedBase, svc.GetShock(1), 4);        // level lands on and holds the raised base α·M, not 0
        Assert.True(svc.AnyActive);                          // entry persists because the residual floor > floor
    }

    // ---- (d) accumulated residual clamped to the geometric band ----

    [Fact]
    public void Accumulated_residual_is_bounded_by_cap_and_the_fundamental_band()
    {
        // A relentless same-sign barrage (α=0.9) drives the permanent floor up, but the joint soft-wall + hard clamp
        // keep the TOTAL (and hence the residual) within ±Cap at all times.
        const double cap = 0.06;
        var src = new StepSource { Emit = { new ShockImpulse(1, 0.05, 0.9, 300.0) } };
        var svc = BuildSvc(src, cap: cap, halfLife: 300.0, residualHalfLife: 1e9);
        svc.Reset(T0);
        for (int i = 1; i <= 600; i++)
        {
            svc.Tick(T0.AddSeconds(i));
            Assert.InRange(svc.GetShock(1), 0.0, cap + 1e-9);      // total never exceeds +Cap
            Assert.InRange(svc.GetTransient(1), 0.0, cap + 1e-9);  // residual = total − transient ⇒ also ≤ Cap
        }
        double maxShock = svc.GetShock(1);
        Assert.True(maxShock > 0.0);

        // Feed the accumulated shock through the FundamentalService anchor: clamped to seed × (1 + (Band + Cap)).
        var fund = new FundamentalService(BuildStocks((1, 100m)).Object, new StockProfileService(enabled: false),
            NullLogger<FundamentalService>.Instance, enabled: true, band: 0.12m, theta: 0.02, sigma: 0.0,
            driftIntervalSec: 60.0, exogShock: _ => maxShock, anyShockActive: () => true, shockCap: (decimal)cap);
        fund.Reset();
        Assert.InRange(fund.Get(1, Usd), 100m, 100m * (1m + 0.12m + (decimal)cap) + 1e-6m);
    }

    [Fact]
    public void Residual_bleeds_back_toward_zero_with_no_further_impulses()
    {
        // With a realistic ResidualHalfLifeSec the permanent floor is session-permanent but NOT eternal.
        var src = new StepSource { Emit = { new ShockImpulse(1, 0.04, 0.8, 300.0) } };
        var svc = BuildSvc(src, cap: 0.06, residualHalfLife: 100.0); // short residual life for a fast test
        svc.Reset(T0);
        svc.Tick(T0.AddSeconds(1));
        src.Emit = new List<ShockImpulse>();
        for (int i = 1; i <= 60; i++) svc.Tick(T0.AddSeconds(1 + i * 60)); // dt clamped to 60s; ~36 residual half-lives
        Assert.Equal(0.0, svc.GetShock(1)); // both components below floor ⇒ dropped ⇒ no unbounded ratchet
    }

    // ---- (e) aftershock scheduling: Poisson follow-ups, MaxDepth 1 ----

    [Fact]
    public void SamplePoisson_mean_matches_lambda()
    {
        var rng = new Random(7);
        Assert.Equal(0, BotMath.SamplePoisson(rng, 0.0));
        Assert.Equal(0, BotMath.SamplePoisson(rng, -1.0));
        double sum = 0; int n = 200000;
        for (int i = 0; i < n; i++) sum += BotMath.SamplePoisson(rng, 0.6);
        Assert.InRange(sum / n, 0.58, 0.62);
    }

    [Fact]
    public void Aftershocks_fire_when_lambda_positive_and_are_inert_at_zero()
    {
        var stocks = BuildStocks((1, 100m)).Object;

        RandomShockSource Make(double lambda) => new(stocks, 0.5, 0.01, 0.06, 1.8,
            permanence: new NewsPermanenceOptions { Enabled = true, AfterLambda = lambda });

        var inert = Make(0.0); inert.Reset();
        var live = Make(0.6); live.Reset();
        for (int t = 1; t <= 6000; t++) { inert.Poll(t, 1.0); live.Poll(t, 1.0); }

        Assert.True(inert.BaseEventCount > 0);
        Assert.Equal(0, inert.AftershockCount);      // Lambda=0 ⇒ no follow-ups
        Assert.True(live.BaseEventCount > 0);
        Assert.True(live.AftershockCount > 0);        // Lambda>0 ⇒ follow-ups fire
    }

    [Fact]
    public void Aftershock_count_stays_bounded_by_lambda_no_grandchildren_cascade()
    {
        // MaxDepth=1: aftershocks NEVER schedule their own follow-ups, so over many base events the emitted
        // aftershock total tracks E[Poisson] = Lambda·LambdaAfter (Individual tier 0.6), NOT an exponential cascade.
        var stocks = BuildStocks((1, 100m)).Object;
        var src = new RandomShockSource(stocks, 0.5, 0.01, 0.06, 1.8,
            permanence: new NewsPermanenceOptions { Enabled = true, AfterLambda = 1.0, AfterMaxDepth = 1 });
        src.Reset();
        for (int t = 1; t <= 40000; t++) src.Poll(t, 1.0);
        double ratio = (double)src.AftershockCount / src.BaseEventCount;
        // Expected ≈ 1.0 · 0.6 = 0.6; a cascade would blow far past 1. Loose band absorbs Poisson variance.
        Assert.InRange(ratio, 0.45, 0.80);
    }

    [Fact]
    public void MaxDepth_zero_disables_aftershocks_entirely()
    {
        var stocks = BuildStocks((1, 100m)).Object;
        var src = new RandomShockSource(stocks, 0.5, 0.01, 0.06, 1.8,
            permanence: new NewsPermanenceOptions { Enabled = true, AfterLambda = 1.0, AfterMaxDepth = 0 });
        src.Reset();
        for (int t = 1; t <= 6000; t++) src.Poll(t, 1.0);
        Assert.True(src.BaseEventCount > 0);
        Assert.Equal(0, src.AftershockCount);
    }
}
