using KieshStockExchange.Models;
using KieshStockExchange.Services.BackgroundServices.Helpers;
using Style = KieshStockExchange.Services.BackgroundServices.Helpers.AiBotDecisionService.ExtremeReactionStyle;
using Dir = KieshStockExchange.Services.BackgroundServices.Helpers.AiBotDecisionService.ExtremeDirection;

namespace KieshStockExchange.Tests;

/// <summary>
/// Down-drift fix #1: the only one-sided extreme style was Panic (sells on BOTH bull and bear overflow),
/// with no buy-both mirror — so every Scalper default and the random pool leaned net-sell. The Greed style
/// (buys both sides) is Panic's mirror; with the flag on, half the Scalpers (a stable hash split) flip to
/// Greed and the random pool gains Greed, so aggregate extreme-reaction flow nets to ~zero. Flag off must
/// reproduce today's behavior exactly (every Scalper = Panic, pool = Next(4)).
/// </summary>
public class GreedReactionTests
{
    private static int DirVal(Dir d) => d switch { Dir.Buy => 1, Dir.Sell => -1, _ => 0 };

    // Net signed flow a style contributes across a balanced (one bull + one bear) overflow sweep.
    private static int Net(Style s) =>
        DirVal(AiBotDecisionService.BullDirection(s)) + DirVal(AiBotDecisionService.BearDirection(s));

    [Fact]
    public void Greed_buys_on_both_sides_mirroring_Panic()
    {
        Assert.Equal(Dir.Buy, AiBotDecisionService.BullDirection(Style.Greed)); // chase the rally
        Assert.Equal(Dir.Buy, AiBotDecisionService.BearDirection(Style.Greed)); // buy the dip
        Assert.Equal(+2, Net(Style.Greed));   // exact buy-both mirror …
        Assert.Equal(-2, Net(Style.Panic));   // … of Panic's sell-both
        Assert.Equal(0, Net(Style.FOMO));     // FOMO/Contrarian already net to zero
        Assert.Equal(0, Net(Style.Contrarian));
    }

    [Fact]
    public void Scalper_default_splits_evenly_between_Panic_and_Greed_when_on()
    {
        const int N = 20_000;
        int greed = 0, panic = 0;
        for (int id = 1; id <= N; id++)
        {
            var s = AiBotDecisionService.DefaultExtremeStyle(AiStrategy.Scalper, id, greed: true, split: 0.5m);
            if (s == Style.Greed) greed++;
            else if (s == Style.Panic) panic++;
            else Assert.Fail($"unexpected style {s}");
        }
        Assert.Equal(N, greed + panic);
        Assert.InRange((double)greed / N, 0.47, 0.53); // stable hash splits the cohort ~50/50
    }

    [Fact]
    public void Scalper_cohort_flow_is_neutral_when_on_and_net_sell_when_off()
    {
        const int N = 20_000;
        long onSum = 0, offSum = 0;
        for (int id = 1; id <= N; id++)
        {
            onSum  += Net(AiBotDecisionService.DefaultExtremeStyle(AiStrategy.Scalper, id, greed: true,  split: 0.5m));
            offSum += Net(AiBotDecisionService.DefaultExtremeStyle(AiStrategy.Scalper, id, greed: false, split: 0.5m));
        }
        Assert.Equal(-2L * N, offSum);              // the bug: every Scalper = Panic ⇒ pure sell pressure
        Assert.True(System.Math.Abs(onSum) < N / 10, $"cohort should be ~neutral, got {onSum}"); // the fix
    }

    [Fact]
    public void Random_pool_is_neutral_when_on_and_net_sell_when_off()
    {
        // Out-of-character pool: with Greed it is {FOMO,Contrarian,Panic,Greed,None} → nets to zero;
        // without it the original {FOMO,Contrarian,Panic,None} carries an unmirrored Panic sell.
        var poolOn  = new[] { Style.FOMO, Style.Contrarian, Style.Panic, Style.Greed, Style.None };
        var poolOff = new[] { Style.FOMO, Style.Contrarian, Style.Panic, Style.None };
        Assert.Equal(0, poolOn.Sum(Net));
        Assert.Equal(-2, poolOff.Sum(Net));
    }

    [Fact]
    public void Flag_off_is_byte_identical_to_old_defaults()
    {
        for (int id = 1; id <= 1_000; id++)
        {
            // Scalper is Panic for every id, independent of the split, when greed is off.
            Assert.Equal(Style.Panic, AiBotDecisionService.DefaultExtremeStyle(AiStrategy.Scalper, id, greed: false, split: 0.5m));
            Assert.Equal(Style.Panic, AiBotDecisionService.DefaultExtremeStyle(AiStrategy.Scalper, id, greed: false, split: 0.9m));
            // Other strategies are unchanged in either mode.
            Assert.Equal(Style.FOMO,       AiBotDecisionService.DefaultExtremeStyle(AiStrategy.TrendFollower, id, greed: true, split: 0.5m));
            Assert.Equal(Style.Contrarian, AiBotDecisionService.DefaultExtremeStyle(AiStrategy.MeanReversion, id, greed: true, split: 0.5m));
            Assert.Equal(Style.Contrarian, AiBotDecisionService.DefaultExtremeStyle(AiStrategy.MarketMaker,   id, greed: true, split: 0.5m));
            Assert.Equal(Style.None,       AiBotDecisionService.DefaultExtremeStyle(AiStrategy.Random,        id, greed: true, split: 0.5m));
        }
    }
}
