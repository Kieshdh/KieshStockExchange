using System;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

/// <summary>
/// §elastic anchor (realism overhaul step 2): pins the pure nonlinear soft-wall tilt. Zero restoring pull within
/// ±Deadband (small intraday + multi-day moves wander free = no per-tick snap-back), then strength·sign·(excess/scale)^power
/// beyond the band (power&gt;1 ⇒ gentle just past the band, stiff far out = the elastic band + a sell-driver on big
/// up-runs). Signed + magnitude-symmetric. Elastic=false uses the untouched linear path (byte-identical) — not exercised here.
/// </summary>
public class ElasticAnchorTiltTests
{
    const decimal Deadband = 0.20m, Scale = 0.12m, Strength = 0.40m, Power = 3.0m;

    [Fact]
    public void Zero_within_deadband()
        => Assert.Equal(0m, AiBotDecisionService.ElasticAnchorTilt(0.10m, Deadband, Scale, Strength, Power));

    [Fact]
    public void Zero_at_deadband_edge()
        => Assert.Equal(0m, AiBotDecisionService.ElasticAnchorTilt(0.20m, Deadband, Scale, Strength, Power));

    [Fact]
    public void Fires_beyond_deadband_signed_and_symmetric()
    {
        var up = AiBotDecisionService.ElasticAnchorTilt(0.30m, Deadband, Scale, Strength, Power);
        var down = AiBotDecisionService.ElasticAnchorTilt(-0.30m, Deadband, Scale, Strength, Power);
        Assert.True(up > 0m);          // price below anchor (dip) ⇒ positive buy tilt
        Assert.Equal(-up, down);        // sign-symmetric magnitude
    }

    [Fact]
    public void Superlinear_far_out()
    {
        var t40 = AiBotDecisionService.ElasticAnchorTilt(0.40m, Deadband, Scale, Strength, Power);  // excess 0.20
        var t60 = AiBotDecisionService.ElasticAnchorTilt(0.60m, Deadband, Scale, Strength, Power);  // excess 0.40 (2x)
        // linear would give t60 == 2·t40; power 3 gives ~8× ⇒ strictly superlinear (the stiffening far out).
        Assert.True(t60 > t40 * 4m);
    }

    [Fact]
    public void Power_one_matches_linear_excess()
    {
        // power=1 ⇒ tilt == strength·(excess/scale) == the linear dead-banded path (dimensional sanity).
        var elastic = AiBotDecisionService.ElasticAnchorTilt(0.30m, Deadband, Scale, Strength, 1.0m);
        var linear = Strength * (AiBotDecisionService.AnchorDeadband(0.30m, Deadband) / Scale);
        Assert.True(Math.Abs(elastic - linear) < 0.0001m);
    }
}
