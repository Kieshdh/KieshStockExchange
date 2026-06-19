using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// §exogenous-information: pure-static tests for the shared deterministic primitives (<see cref="BotMath"/>)
/// and the chaser seam (<see cref="AiBotDecisionService.IsChaser"/> / <see cref="AiBotDecisionService.ChaserResponse"/>).
/// Mirrors the RegimeDriftTests / DirectionalBiasTests pure-static style — no RNG, no I/O.
/// </summary>
public class BotMathTests
{
    [Fact]
    public void HashUnit01_single_is_in_range_and_deterministic()
    {
        for (int id = -5; id < 50; id++)
        {
            var a = BotMath.HashUnit01(id);
            Assert.InRange(a, 0.0, 0.9999999999);
            Assert.Equal(a, BotMath.HashUnit01(id)); // pure
        }
        // The regime cohort hash delegates to BotMath, so they must agree bit-for-bit.
        Assert.Equal((decimal)BotMath.HashUnit01(12345), BotRegimeService.StableUnit(12345));
    }

    [Fact]
    public void HashUnit01_two_input_reshuffles_on_second_key()
    {
        // Same a, different b ⇒ (almost surely) different unit — this is what makes the per-(bot,shock) reshuffle work.
        Assert.NotEqual(BotMath.HashUnit01(42, 1), BotMath.HashUnit01(42, 2));
        // Deterministic for a fixed pair.
        Assert.Equal(BotMath.HashUnit01(42, 7), BotMath.HashUnit01(42, 7));
        // (uint)-safe for negative ids and high-bit salts (must not throw / must stay in range).
        var neg = BotMath.HashUnit01(-12345 ^ 0x5A17, 3);
        Assert.InRange(neg, 0.0, 0.9999999999);
    }

    [Fact]
    public void SoftWallStep_matches_regime_shape()
    {
        Assert.Equal(0.0, BotMath.SoftWallStep(0.3, 0.1, 0.0, 0.1));          // cap<=0 ⇒ 0
        Assert.Equal(0.02, BotMath.SoftWallStep(0.0, 0.02, 0.5, 0.1), 12);    // free in the middle
        Assert.Equal(0.45, BotMath.SoftWallStep(0.5, 0.0, 0.5, 0.1), 12);     // pull back near +cap
        Assert.Equal(0.5, BotMath.SoftWallStep(0.4, 10.0, 0.5, 0.1));         // hard clamp at +cap
        Assert.Equal(-0.5, BotMath.SoftWallStep(-0.4, -10.0, 0.5, 0.1));      // hard clamp at -cap
    }

    [Fact]
    public void DrawMagnitude_stays_in_band()
    {
        var rng = new Random(123);
        for (int i = 0; i < 10_000; i++)
            Assert.InRange(BotMath.DrawMagnitude(rng, 0.01, 0.06, 1.8), 0.01, 0.06);
    }

    [Fact]
    public void IsChaser_edges_and_spread()
    {
        Assert.False(AiBotDecisionService.IsChaser(7, 1, 0.0, 0x5A17));  // fraction 0 ⇒ nobody
        Assert.True(AiBotDecisionService.IsChaser(7, 1, 1.0, 0x5A17));   // fraction 1 ⇒ everybody

        // Selecting fraction f over a large id range yields ≈ f of the population (no XOR-collision bug).
        const int n = 20_000;
        int hits = 0;
        for (int id = 1; id <= n; id++)
            if (AiBotDecisionService.IsChaser(id, shockId: 1, fraction: 0.25, salt: 0x5A17)) hits++;
        Assert.InRange((double)hits / n, 0.22, 0.28);

        // Reshuffles per shockId: the chaser set for shockId 1 differs from shockId 2.
        int diff = 0;
        for (int id = 1; id <= n; id++)
            if (AiBotDecisionService.IsChaser(id, 1, 0.25, 0x5A17) != AiBotDecisionService.IsChaser(id, 2, 0.25, 0x5A17))
                diff++;
        Assert.True(diff > n / 10, $"expected a meaningful cohort reshuffle across shock ids, got {diff}");

        // Negative aiUserId + high-bit salt must not throw and must be deterministic.
        Assert.Equal(
            AiBotDecisionService.IsChaser(-99, 4, 0.5, unchecked((int)0x9000_0000)),
            AiBotDecisionService.IsChaser(-99, 4, 0.5, unchecked((int)0x9000_0000)));
    }

    [Fact]
    public void ChaserResponse_is_pure_and_odd_symmetric()
    {
        Assert.Equal(0.0, AiBotDecisionService.ChaserResponse(0.0, 1.0, 0.08), 12);
        Assert.Equal(
            AiBotDecisionService.ChaserResponse(0.05, 0.4, 0.08),
            -AiBotDecisionService.ChaserResponse(-0.05, 0.4, 0.08), 12);
        // strength·tanh(shock/scale)
        Assert.Equal(0.4 * Math.Tanh(0.05 / 0.08), AiBotDecisionService.ChaserResponse(0.05, 0.4, 0.08), 12);
    }
}
