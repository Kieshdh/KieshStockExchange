using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// RsiCalculator.Rsi implements Wilder's RSI: simple-mean seed over the first
/// period of close-to-close gains/losses, then (period-1)-weighted smoothing.
/// Pure gains pin to 100, pure losses to 0, a flat tape reads neutral 50.
/// </summary>
public sealed class RsiCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<Candle> FromCloses(params double[] closes)
    {
        var list = new List<Candle>(closes.Length);
        for (int i = 0; i < closes.Length; i++)
        {
            decimal c = (decimal)closes[i];
            list.Add(new Candle
            {
                OpenTime = T0.AddMinutes(i),
                Open = c, High = c, Low = c, Close = c, Volume = 1,
            });
        }
        return list;
    }

    [Fact]
    public void Rsi_SteadilyRising_Is100()
    {
        var candles = FromCloses(Enumerable.Range(1, 20).Select(i => (double)i).ToArray());
        var rsi = RsiCalculator.Rsi(candles, period: 14);

        Assert.Equal(6, rsi.Count); // first point at index 14, one per candle after
        Assert.Equal(candles[14].OpenTime, rsi[0].Time);
        Assert.All(rsi, p => Assert.Equal(100.0, p.Value, precision: 10));
    }

    [Fact]
    public void Rsi_SteadilyFalling_Is0()
    {
        var candles = FromCloses(Enumerable.Range(1, 20).Select(i => 21.0 - i).ToArray());
        var rsi = RsiCalculator.Rsi(candles, period: 14);

        Assert.Equal(6, rsi.Count);
        Assert.All(rsi, p => Assert.Equal(0.0, p.Value, precision: 10));
    }

    [Fact]
    public void Rsi_FlatSeries_Is50()
    {
        var candles = FromCloses(Enumerable.Repeat(5.0, 20).ToArray());
        var rsi = RsiCalculator.Rsi(candles, period: 14);

        Assert.Equal(6, rsi.Count);
        Assert.All(rsi, p => Assert.Equal(50.0, p.Value, precision: 10));
    }

    [Fact]
    public void Rsi_KnownWorkedExample_MatchesHandComputation()
    {
        // Deltas +1, -0.5, +1 with period 2:
        //   seed avgGain = (1+0)/2 = 0.5, avgLoss = (0+0.5)/2 = 0.25 → RS=2 → RSI = 100-100/3
        //   next avgGain = (0.5·1+1)/2 = 0.75, avgLoss = 0.25/2 = 0.125 → RS=6 → RSI = 100-100/7
        var candles = FromCloses(10.0, 11.0, 10.5, 11.5);
        var rsi = RsiCalculator.Rsi(candles, period: 2);

        Assert.Equal(2, rsi.Count);
        Assert.Equal(candles[2].OpenTime, rsi[0].Time);
        Assert.Equal(100.0 - 100.0 / 3.0, rsi[0].Value, precision: 10);
        Assert.Equal(candles[3].OpenTime, rsi[1].Time);
        Assert.Equal(100.0 - 100.0 / 7.0, rsi[1].Value, precision: 10);
    }

    [Fact]
    public void Rsi_ShortInput_ReturnsEmpty()
    {
        // period deltas need period+1 candles; exactly period candles is still too short.
        var candles = FromCloses(Enumerable.Range(1, 14).Select(i => (double)i).ToArray());
        Assert.Empty(RsiCalculator.Rsi(candles, period: 14));
        Assert.Empty(RsiCalculator.Rsi(new List<Candle>(), period: 14));
        Assert.Empty(RsiCalculator.Rsi(null!, period: 14));
    }
}
