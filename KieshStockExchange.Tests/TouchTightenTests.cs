using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// Microstructure bid-ask bounce: TightenOffset pulls a close-tier limit offset toward mid by (1-prc) so the
/// touch tightens and each spread-crossing print zig-zags less. Default 0 (or a non-close tier) is a pass-through
/// ⇒ byte-identical to the pre-flag behaviour; the helper is pure (no RNG, no clock) so the flag adds no draw and
/// the flag-off market is bit-for-bit unchanged. Mirrors RoundSnapTests, pinning the helper directly.
/// </summary>
public class TouchTightenTests
{
    [Theory]
    [InlineData(0.02)]
    [InlineData(0.05)]
    [InlineData(0.0004)]
    public void Off_is_passthrough_byte_identical(decimal offset)
    {
        // prc = 0 ⇒ no-op for close and non-close alike (byte-identical flag-off path).
        Assert.Equal(offset, AiBotDecisionService.TightenOffset(offset, isCloseTier: true,  touchTightenPrc: 0m));
        Assert.Equal(offset, AiBotDecisionService.TightenOffset(offset, isCloseTier: false, touchTightenPrc: 0m));
    }

    [Theory]
    [InlineData(0.06, 0.40, 0.036)]   // 0.06 * (1 - 0.40)
    [InlineData(0.02, 0.50, 0.010)]   // 0.02 * (1 - 0.50)
    [InlineData(0.05, 0.20, 0.040)]   // 0.05 * (1 - 0.20)
    public void On_close_tier_scales_offset_by_one_minus_prc(decimal offset, decimal prc, decimal expected)
    {
        Assert.Equal(expected, AiBotDecisionService.TightenOffset(offset, isCloseTier: true, touchTightenPrc: prc));
    }

    [Fact]
    public void On_close_tier_moves_strictly_nearer_mid()
    {
        var original   = 0.05m;
        var tightened  = AiBotDecisionService.TightenOffset(original, isCloseTier: true, touchTightenPrc: 0.20m);
        Assert.True(tightened < original, "tightening must reduce the close-tier offset toward mid");
        Assert.True(tightened > 0m, "offset stays strictly positive so the limit never crosses mid");
    }

    [Fact]
    public void Non_close_tier_unchanged_even_when_flag_on()
    {
        // Mid/Far standing walls are not touched regardless of prc.
        Assert.Equal(0.08m, AiBotDecisionService.TightenOffset(0.08m, isCloseTier: false, touchTightenPrc: 0.40m));
        Assert.Equal(0.25m, AiBotDecisionService.TightenOffset(0.25m, isCloseTier: false, touchTightenPrc: 0.50m));
    }

    [Fact]
    public void Pure_function_is_deterministic_across_repeated_calls()
    {
        var a = AiBotDecisionService.TightenOffset(0.075m, isCloseTier: true, touchTightenPrc: 0.25m);
        var b = AiBotDecisionService.TightenOffset(0.075m, isCloseTier: true, touchTightenPrc: 0.25m);
        Assert.Equal(a, b);
        Assert.Equal(0.05625m, a);   // 0.075 * (1 - 0.25)
    }

    [Theory]
    [InlineData(0.05, 1.0, 0.0)]     // full compression ⇒ at mid
    [InlineData(0.0,  0.40, 0.0)]    // zero offset stays zero
    public void Boundaries_behave(decimal offset, decimal prc, decimal expected)
    {
        Assert.Equal(expected, AiBotDecisionService.TightenOffset(offset, isCloseTier: true, touchTightenPrc: prc));
    }
}
