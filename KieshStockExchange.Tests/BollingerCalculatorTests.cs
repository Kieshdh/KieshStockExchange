using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// BollingerCalculator.Bollinger: middle band = SMA of closes, envelope at
/// ±k population standard deviations, first point at index period-1.
/// </summary>
public sealed class BollingerCalculatorTests
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
    public void Bollinger_FlatSeries_BandsCollapseToMiddle()
    {
        var candles = FromCloses(Enumerable.Repeat(7.0, 10).ToArray());
        var bands = BollingerCalculator.Bollinger(candles, period: 3, k: 2.0);

        Assert.Equal(8, bands.Count); // first point at index 2
        Assert.Equal(candles[2].OpenTime, bands[0].Time);
        Assert.All(bands, p =>
        {
            Assert.Equal(7.0, p.Middle, precision: 10);
            Assert.Equal(7.0, p.Upper, precision: 10);
            Assert.Equal(7.0, p.Lower, precision: 10);
        });
    }

    [Fact]
    public void Bollinger_KnownVarianceWindow_MatchesHandComputation()
    {
        // Window {1,2,3}: mean 2, population variance 2/3 ⇒ stdev √(2/3).
        var candles = FromCloses(1.0, 2.0, 3.0);
        var bands = BollingerCalculator.Bollinger(candles, period: 3, k: 2.0);

        double stdev = Math.Sqrt(2.0 / 3.0);
        var p = Assert.Single(bands);
        Assert.Equal(2.0, p.Middle, precision: 10);
        Assert.Equal(2.0 + 2.0 * stdev, p.Upper, precision: 10);
        Assert.Equal(2.0 - 2.0 * stdev, p.Lower, precision: 10);
    }

    [Fact]
    public void Bollinger_Middle_EqualsSmaOfCloses()
    {
        var closes = new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 };
        var candles = FromCloses(closes);
        var bands = BollingerCalculator.Bollinger(candles, period: 3, k: 2.0);

        Assert.Equal(4, bands.Count);
        for (int i = 0; i < bands.Count; i++)
        {
            double sma = (closes[i] + closes[i + 1] + closes[i + 2]) / 3.0;
            Assert.Equal(sma, bands[i].Middle, precision: 10);
            Assert.Equal(candles[i + 2].OpenTime, bands[i].Time);
        }
    }

    [Fact]
    public void Bollinger_ShortInput_ReturnsEmpty()
    {
        Assert.Empty(BollingerCalculator.Bollinger(FromCloses(1.0, 2.0), period: 3));
        Assert.Empty(BollingerCalculator.Bollinger(new List<Candle>(), period: 3));
        Assert.Empty(BollingerCalculator.Bollinger(null!, period: 3));
    }
}
