using KieshStockExchange.Services.BackgroundServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// Down-drift fix #2 (optional robustness layer): the continuous cash-homeostasis controller restores cash
/// smoothly toward the band midpoint — which, after the §8 seed recenter, equals the seed cash% — so the
/// rest-point is the seed (no systematic drift). Hard walls still force buy/sell at the band edges. With the
/// flag off the controller is the original edge-only logic, byte-for-byte.
/// </summary>
public class CashHomeostasisTests
{
    private const decimal Min = 0.10m, Max = 0.30m;     // mid = 0.20, half = 0.10
    private const decimal BuyBias = 0.50m, MaxShift = 0.15m, EdgeBuy = 0.95m, EdgeSell = 0.05m;

    private static decimal Continuous(decimal cashPrc) =>
        AiBotDecisionService.CashHomeostasis(BuyBias, cashPrc, Min, Max,
            continuous: true, maxShift: MaxShift, edgeBuy: EdgeBuy, edgeSell: EdgeSell);

    private static decimal EdgeOnly(decimal cashPrc) =>
        AiBotDecisionService.CashHomeostasis(BuyBias, cashPrc, Min, Max,
            continuous: false, maxShift: MaxShift, edgeBuy: EdgeBuy, edgeSell: EdgeSell);

    [Fact]
    public void Continuous_restores_toward_the_midpoint()
    {
        Assert.Equal(BuyBias, Continuous(0.20m));        // at mid → no shift
        Assert.True(Continuous(0.25m) > BuyBias);        // cash-rich above mid → buy more (spend down)
        Assert.True(Continuous(0.15m) < BuyBias);        // cash-poor below mid → buy less (build up)
        Assert.Equal(BuyBias + MaxShift * 0.5m, Continuous(0.25m)); // gentle linear in-band shift
    }

    [Fact]
    public void Continuous_forces_hard_at_the_walls()
    {
        Assert.True(Continuous(Max) >= EdgeBuy);   // excess cash at/over Max → forced buy
        Assert.True(Continuous(0.40m) >= EdgeBuy);
        Assert.True(Continuous(Min) <= EdgeSell);  // starved at/under Min → forced no-buy
        Assert.True(Continuous(0.02m) <= EdgeSell);
    }

    [Fact]
    public void Flag_off_reproduces_the_old_edge_only_controller()
    {
        // In-band: flat — the bias is untouched.
        Assert.Equal(BuyBias, EdgeOnly(0.20m));
        Assert.Equal(BuyBias, EdgeOnly(Min));
        Assert.Equal(BuyBias, EdgeOnly(Max));

        // Below Min / above Max: the original 0.40 distance-normalized push, recomputed here verbatim.
        const decimal old = 0.40m;
        var belowCash = 0.05m;
        var belowExpected = BuyBias - old * ((Min - belowCash) / Min);
        Assert.Equal(belowExpected, EdgeOnly(belowCash));

        var aboveCash = 0.50m;
        var aboveExpected = BuyBias + old * ((aboveCash - Max) / (1m - Max));
        Assert.Equal(aboveExpected, EdgeOnly(aboveCash));
    }
}
