using System;
using System.Linq;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// FX-damp lever (Bots:Fx:*) — determinism + byte-identical-off + bound tests for the AR(1) mid-rate
/// walker. The tunables (Alpha/Amplitude/ConvertSpread/RateBand) moved from consts to config-bound
/// static fields; the defaults must reproduce the historical walk byte-for-byte, and the damped arm
/// (higher Alpha, lower Amplitude, tighter RateBand) must stay a bounded mean-reverting walk.
///
/// The tunables are process-global statics (like <see cref="MidReference"/>), so this suite runs
/// serial and restores the historical defaults in the ctor and Dispose.
/// </summary>
[Collection("FxRateSerial")]
public sealed class FxRateServiceTests : IDisposable
{
    // The historical constants — the byte-identical baseline every test resets to.
    private const decimal DefAlpha = 0.92m, DefAmp = 0.005m, DefSpread = 0.001m, DefBand = 0.20m;
    private const decimal BaseMid = 1.08m;   // FX_BASE_RATES[(EUR, USD)]
    private const int RngSeed = 47;

    public FxRateServiceTests() => FxRateService.ConfigureForTests(DefAlpha, DefAmp, DefSpread, DefBand);
    public void Dispose() => FxRateService.ConfigureForTests(DefAlpha, DefAmp, DefSpread, DefBand);

    private static FxRateService NewService() => new(NullLogger<FxRateService>.Instance);

    /// <summary>Step the EUR/USD mid <paramref name="steps"/> times (Tick spaced &gt; the 60s interval).</summary>
    private static decimal[] DriveEurUsd(FxRateService svc, int steps)
    {
        var t0 = TimeHelper.NowUtc();
        var mids = new decimal[steps];
        for (var i = 0; i < steps; i++)
        {
            svc.Tick(t0 + TimeSpan.FromSeconds(120 * (i + 1)));
            mids[i] = svc.GetMidRate(CurrencyType.EUR, CurrencyType.USD);
        }
        return mids;
    }

    /// <summary>The reference AR(1) walk from the historical constants — the byte-identical target.</summary>
    private static decimal[] ReferenceSequence(int steps, decimal alpha, decimal amp, decimal band)
    {
        var rng = new Random(RngSeed);
        var mids = new decimal[steps];
        var prev = BaseMid;                                  // Reset seeds the mid at base
        for (var i = 0; i < steps; i++)
        {
            var noise = (decimal)(rng.NextDouble() * 2.0 - 1.0);
            var raw = alpha * prev + (1m - alpha) * BaseMid + amp * BaseMid * noise;
            var lo = BaseMid * (1m - band);
            var hi = BaseMid * (1m + band);
            if (raw < lo) raw = lo;
            if (raw > hi) raw = hi;
            mids[i] = raw;
            prev = raw;
        }
        return mids;
    }

    // ---- byte-identical defaults + determinism --------------------------------------------------

    [Fact]
    public void Default_config_reproduces_the_historical_ar1_walk_byte_for_byte()
    {
        // Un-configured service (defaults restored in ctor) must equal the reference formula run with
        // the historical constants — guards against a silent default drift in the const→field refactor.
        var mids = DriveEurUsd(NewService(), 20);
        Assert.Equal(ReferenceSequence(20, DefAlpha, DefAmp, DefBand), mids);
    }

    [Fact]
    public void Walk_is_deterministic_across_instances()
    {
        Assert.Equal(DriveEurUsd(NewService(), 20), DriveEurUsd(NewService(), 20));
    }

    [Fact]
    public void Initial_mid_before_any_tick_is_the_base_rate()
    {
        Assert.Equal(BaseMid, NewService().GetMidRate(CurrencyType.EUR, CurrencyType.USD));
    }

    // ---- damped arm: different walk, tighter bound ----------------------------------------------

    [Fact]
    public void Damped_config_changes_the_walk_and_holds_the_tighter_band()
    {
        // The council's damped candidate: smoother (Alpha↑), less volatile (Amplitude↓), tighter band.
        FxRateService.ConfigureForTests(alpha: 0.97m, amplitude: 0.002m, convertSpread: 0.001m, rateBand: 0.05m);
        var mids = DriveEurUsd(NewService(), 40);

        // A real, different sequence from the default walk...
        Assert.NotEqual(ReferenceSequence(40, DefAlpha, DefAmp, DefBand), mids);
        // ...and matches its own reference (config is honoured), staying inside the ±5% clamp.
        Assert.Equal(ReferenceSequence(40, 0.97m, 0.002m, 0.05m), mids);
        Assert.All(mids, m => Assert.InRange(m, BaseMid * 0.95m, BaseMid * 1.05m));
    }

    [Fact]
    public void RateBand_hard_clamps_a_high_amplitude_walk()
    {
        // Huge Amplitude with no mean-reversion pull would run away; the band must contain it.
        FxRateService.ConfigureForTests(alpha: 1.0m, amplitude: 0.5m, convertSpread: 0.001m, rateBand: 0.03m);
        var mids = DriveEurUsd(NewService(), 60);
        Assert.All(mids, m => Assert.InRange(m, BaseMid * 0.97m, BaseMid * 1.03m));
    }

    // ---- spread + rate identities ---------------------------------------------------------------

    [Fact]
    public void GetBidAsk_brackets_the_mid_by_the_configured_spread()
    {
        var svc = NewService();
        var mid = svc.GetMidRate(CurrencyType.EUR, CurrencyType.USD);
        var (bid, ask) = svc.GetBidAsk(CurrencyType.EUR, CurrencyType.USD);
        Assert.Equal(mid * (1m - DefSpread), bid);
        Assert.Equal(mid * (1m + DefSpread), ask);
    }

    [Fact]
    public void Narrowing_the_spread_tightens_the_bid_ask()
    {
        FxRateService.ConfigureForTests(DefAlpha, DefAmp, convertSpread: 0.0002m, rateBand: DefBand);
        var svc = NewService();
        var mid = svc.GetMidRate(CurrencyType.EUR, CurrencyType.USD);
        var (bid, ask) = svc.GetBidAsk(CurrencyType.EUR, CurrencyType.USD);
        Assert.Equal(mid * 0.9998m, bid);
        Assert.Equal(mid * 1.0002m, ask);
        Assert.True(ask - bid < mid * 2m * DefSpread);   // strictly tighter than the default round-trip
    }

    [Fact]
    public void GetMidRate_is_identity_for_same_currency_and_inverts_the_pair()
    {
        var svc = NewService();
        Assert.Equal(1m, svc.GetMidRate(CurrencyType.USD, CurrencyType.USD));
        var fwd = svc.GetMidRate(CurrencyType.EUR, CurrencyType.USD);
        Assert.Equal(1m / fwd, svc.GetMidRate(CurrencyType.USD, CurrencyType.EUR));
    }
}

/// <summary>Serial collection so the process-global FX tunables can't leak across parallel tests.</summary>
[CollectionDefinition("FxRateSerial", DisableParallelization = true)]
public sealed class FxRateSerialCollection { }
