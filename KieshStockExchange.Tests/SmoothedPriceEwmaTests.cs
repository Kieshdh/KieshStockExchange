using KieshStockExchange.Services.BackgroundServices;

namespace KieshStockExchange.Tests;

/// <summary>
/// Time-based SmoothedPrices EWMA keep weight (AiTradeService.TimeEwmaKeep) — the engine lever that
/// decouples bot price-perception from same-minute impact (targets the ret_acf_lag1 ceiling).
/// keep = 0.5^(dt/halfLife); dt≤0 or halfLife≤0 ⇒ keep 1 (legacy/off path → no time decay).
/// </summary>
public class SmoothedPriceEwmaTests
{
    [Fact]
    public void Off_or_no_elapsed_keeps_old_fully()
    {
        Assert.Equal(1.0, AiTradeService.TimeEwmaKeep(5.0, 0.0));   // halfLife 0 ⇒ legacy/off
        Assert.Equal(1.0, AiTradeService.TimeEwmaKeep(0.0, 60.0));  // first quote (dt 0) ⇒ keep old
        Assert.Equal(1.0, AiTradeService.TimeEwmaKeep(-3.0, 60.0)); // clock skew ⇒ no decay
    }

    [Fact]
    public void One_half_life_keeps_half()
        => Assert.Equal(0.5, AiTradeService.TimeEwmaKeep(60.0, 60.0), 12);

    [Fact]
    public void Two_half_lives_keep_quarter()
        => Assert.Equal(0.25, AiTradeService.TimeEwmaKeep(120.0, 60.0), 12);

    [Fact]
    public void Longer_gap_weights_new_price_more()
    {
        // keep decreases monotonically with dt ⇒ a longer gap blends in more of the fresh quote.
        Assert.True(AiTradeService.TimeEwmaKeep(10.0, 60.0) > AiTradeService.TimeEwmaKeep(30.0, 60.0));
        Assert.True(AiTradeService.TimeEwmaKeep(30.0, 60.0) > AiTradeService.TimeEwmaKeep(90.0, 60.0));
    }
}
