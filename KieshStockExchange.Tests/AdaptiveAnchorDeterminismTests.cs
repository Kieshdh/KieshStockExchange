using KieshStockExchange.Services.BackgroundServices.Helpers;
using Xunit;

namespace KieshStockExchange.Tests;

// §adaptive (path-dependent) anchor — determinism + byte-identical-off guards. Mirrors
// CoMovementDeterminismTests: pure-function pins on the static anchor math, no RNG, no reseed.
public class AdaptiveAnchorDeterminismTests
{
    [Fact]
    public void BlendZero_returns_seed_exactly()
    {
        // BlendWeight 0 = no re-rate ⇒ the cap anchor IS the seed (byte-identical to CapFromSeed),
        // even when the fast EWMA has run far away.
        for (decimal seed = 10m; seed <= 200m; seed += 10m)
            Assert.Equal(seed, BotPriceMemoryService.AdaptiveAnchorValue(seed, seed * 1.5m, 0m, 0.35m));
    }

    [Fact]
    public void Fast_nonpositive_falls_back_to_seed()
    {
        Assert.Equal(100m, BotPriceMemoryService.AdaptiveAnchorValue(100m, 0m, 1m, 0.35m));
        Assert.Equal(100m, BotPriceMemoryService.AdaptiveAnchorValue(100m, -5m, 1m, 0.35m));
    }

    [Fact]
    public void Anchor_never_escapes_total_excursion_band()
    {
        const decimal seed = 100m, exc = 0.35m;
        // Even at full blend with a runaway fast EWMA, the anchor stays inside seed × [1 ± exc].
        for (decimal fast = 1m; fast <= 1000m; fast += 7m)
        {
            var a = BotPriceMemoryService.AdaptiveAnchorValue(seed, fast, 1m, exc);
            Assert.InRange(a, seed * (1m - exc), seed * (1m + exc));
        }
    }

    [Fact]
    public void Blend_interpolates_between_seed_and_clamped_fast()
    {
        const decimal seed = 100m, exc = 0.5m, fast = 120m; // fast inside the band
        Assert.Equal(seed,                        BotPriceMemoryService.AdaptiveAnchorValue(seed, fast, 0m,   exc));
        Assert.Equal(seed + 0.5m * (fast - seed), BotPriceMemoryService.AdaptiveAnchorValue(seed, fast, 0.5m, exc));
        Assert.Equal(fast,                        BotPriceMemoryService.AdaptiveAnchorValue(seed, fast, 1m,   exc));
    }

    [Fact]
    public void Clamp_applies_before_blend()
    {
        // fast above the band: clamp to seed×1.35, then half-blend toward it.
        const decimal seed = 100m, exc = 0.35m;
        var expected = seed + 0.5m * (seed * 1.35m - seed); // = 117.5
        Assert.Equal(expected, BotPriceMemoryService.AdaptiveAnchorValue(seed, 500m, 0.5m, exc));
    }

    [Fact]
    public void Is_deterministic_same_inputs_same_output()
    {
        for (int i = 1; i <= 200; i++)
        {
            decimal seed = 50m + i, fast = seed * 1.2m;
            Assert.Equal(
                BotPriceMemoryService.AdaptiveAnchorValue(seed, fast, 0.5m, 0.35m),
                BotPriceMemoryService.AdaptiveAnchorValue(seed, fast, 0.5m, 0.35m));
        }
    }

    [Fact]
    public void Fast_ewma_step_never_overshoots()
    {
        // The fast EWMA interpolates toward the fresh price — never past either endpoint.
        Assert.InRange(BotPriceMemoryService.EwmaStep(100m, 110m, 30.0, 900.0), 100m, 110m);
        Assert.InRange(BotPriceMemoryService.EwmaStep(110m, 100m, 30.0, 900.0), 100m, 110m);
    }
}
