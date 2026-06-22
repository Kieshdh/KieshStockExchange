using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §perceived-price desync determinism tests for the pure helpers behind <c>Bots:PerceivedPriceDesync</c>
/// (<see cref="AiBotContext.PerceivedAlpha"/> + <see cref="AiBotContext.PerceivedStep"/>) — the per-bot
/// perceived-price EWMA that supersedes DirectionalReactionLag. The contract the soak relies on:
///   • PURE — no RNG, no wall-clock; identical inputs ⇒ identical outputs (seed reproducibility holds ON).
///   • NO STARTUP TRANSIENT — seeded at <c>live</c> on first sight ⇒ the first-tick slope is exactly 0 (no jolt).
///   • DISPERSION — same-Lateness bots get DIFFERENT alphas via the salted id hash (the ingredient the
///     Lateness-only DirectionalReactionLag lacked), and every alpha stays inside [MinAlpha, MaxAlpha].
///   • MONOTONE — for a fixed bot, alpha decreases as Lateness rises (L=0 fastest, L=1 slowest).
/// Pinning these covers the behavioural surface of the desync math without standing up the full bot loop.
/// </summary>
public class PerceivedPriceDesyncDeterminismTests
{
    private const decimal MinA = 0.05m;
    private const decimal MaxA = 0.45m;
    private const int SaltFast = 0x70E1;
    private const int SaltSlow = 0x1D2B;

    [Fact]
    public void Helpers_are_pure_repeated_calls_match()
    {
        var rng = new Random(4242);
        for (int i = 0; i < 5000; i++)
        {
            int id          = rng.Next(0, 1_000_000);
            decimal L       = (decimal)rng.NextDouble();
            decimal prev    = (decimal)(rng.NextDouble() * 1000.0);
            decimal live    = (decimal)(rng.NextDouble() * 1000.0);

            var a1 = AiBotContext.PerceivedAlpha(L, id, SaltFast, MinA, MaxA);
            var a2 = AiBotContext.PerceivedAlpha(L, id, SaltFast, MinA, MaxA);
            Assert.Equal(a1, a2); // no hidden state / RNG ⇒ reproducible

            var s1 = AiBotContext.PerceivedStep(prev, live, a1);
            var s2 = AiBotContext.PerceivedStep(prev, live, a1);
            Assert.Equal(s1, s2);
        }
    }

    [Fact]
    public void First_sight_seeds_at_live_so_first_slope_is_zero()
    {
        // The accessor seeds prev := live on first sight, so the first EWMA step returns live for ANY alpha ⇒
        // (live − perceived)/perceived == 0 on the opening tick: no synthetic jolt when the flag flips on.
        foreach (var live in new[] { 1m, 12.5m, 999.99m })
            foreach (var L in new[] { 0m, 0.37m, 1m })
            {
                var alpha = AiBotContext.PerceivedAlpha(L, 7, SaltFast, MinA, MaxA);
                Assert.Equal(live, AiBotContext.PerceivedStep(prev: live, live: live, alpha: alpha));
            }
    }

    [Fact]
    public void Converges_monotonically_toward_a_constant_live()
    {
        // Held at a constant live above the seed, the perceived price rises monotonically toward it and the
        // gap shrinks every step — the smear decays, it never overshoots (0 < alpha < 1).
        const decimal seed = 100m, live = 110m;
        var alpha = AiBotContext.PerceivedAlpha(0.5m, 123, SaltFast, MinA, MaxA);
        decimal p = seed, prevGap = decimal.MaxValue;
        for (int t = 0; t < 50; t++)
        {
            p = AiBotContext.PerceivedStep(p, live, alpha);
            Assert.InRange(p, seed, live);            // stays between seed and target (no overshoot)
            var gap = live - p;
            Assert.True(gap < prevGap);               // strictly closing
            prevGap = gap;
        }
        Assert.True(live - p < 0.01m);                // effectively converged
    }

    [Fact]
    public void Same_lateness_bots_disperse_and_stay_in_band()
    {
        // The headline property: at IDENTICAL Lateness, distinct bots get distinct alphas (salt works), and every
        // alpha is inside [MinAlpha, MaxAlpha]. A near-degenerate spread would mean the cohort still moves together.
        const decimal L = 0.5m;
        var seen = new HashSet<decimal>();
        for (int id = 0; id < 2000; id++)
        {
            var a = AiBotContext.PerceivedAlpha(L, id, SaltFast, MinA, MaxA);
            Assert.InRange(a, MinA, MaxA);
            seen.Add(a);
        }
        Assert.True(seen.Count > 1500, $"expected wide dispersion at fixed Lateness, got {seen.Count} distinct alphas");
    }

    [Fact]
    public void Fast_and_slow_salts_give_independent_dispersion()
    {
        // The two perceived series must not collapse into one: for the same (bot, Lateness) the fast and slow
        // alphas differ for the vast majority of bots, so the two EWMA-gap slopes carry distinct information.
        int differ = 0;
        for (int id = 0; id < 1000; id++)
        {
            var f = AiBotContext.PerceivedAlpha(0.3m, id, SaltFast, MinA, MaxA);
            var s = AiBotContext.PerceivedAlpha(0.3m, id, SaltSlow, MinA, MaxA);
            if (f != s) differ++;
        }
        Assert.True(differ > 950, $"fast/slow salts should differ for nearly all bots, differed for {differ}/1000");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(2718)]
    [InlineData(999983)]
    public void Alpha_is_monotone_decreasing_in_lateness_for_a_fixed_bot(int id)
    {
        // For a FIXED bot the salted hash is constant, so alpha = MaxA − (0.5L + const)·(MaxA − MinA) is strictly
        // decreasing in Lateness: low-L bots react fast, high-L bots slow — the per-bot half-life dispersion.
        decimal prev = decimal.MaxValue;
        for (decimal L = 0m; L <= 1m; L += 0.05m)
        {
            var a = AiBotContext.PerceivedAlpha(L, id, SaltFast, MinA, MaxA);
            Assert.True(a < prev, $"alpha should fall as Lateness rises (id={id}, L={L})");
            prev = a;
        }
    }
}
