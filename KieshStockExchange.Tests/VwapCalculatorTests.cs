using KieshStockExchange.Models;
using KieshStockExchange.Services.MarketDataServices.Helpers;

namespace KieshStockExchange.Tests;

/// <summary>
/// VwapCalculator.Vwap: cumulative Σ(typicalPrice·volume)/Σ(volume) with
/// typical price (H+L+C)/3, one point per candle; zero cumulative volume
/// falls back to the typical price.
/// </summary>
public sealed class VwapCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Candle Make(int i, double high, double low, double close, long volume) => new()
    {
        OpenTime = T0.AddMinutes(i),
        Open = (decimal)low, High = (decimal)high, Low = (decimal)low, Close = (decimal)close,
        Volume = volume,
    };

    private static double Tp(double high, double low, double close) => (high + low + close) / 3.0;

    [Fact]
    public void Vwap_EqualVolumes_IsRunningMeanOfTypicalPrices()
    {
        var candles = new List<Candle>
        {
            Make(0, 12, 8, 10, 5),
            Make(1, 22, 18, 20, 5),
            Make(2, 16, 12, 14, 5),
        };
        var vwap = VwapCalculator.Vwap(candles);

        double tp0 = Tp(12, 8, 10), tp1 = Tp(22, 18, 20), tp2 = Tp(16, 12, 14);
        Assert.Equal(3, vwap.Count);
        Assert.Equal(tp0, vwap[0].Value, precision: 10);
        Assert.Equal((tp0 + tp1) / 2.0, vwap[1].Value, precision: 10);
        Assert.Equal((tp0 + tp1 + tp2) / 3.0, vwap[2].Value, precision: 10);
        Assert.Equal(candles[2].OpenTime, vwap[2].Time);
    }

    [Fact]
    public void Vwap_VolumeWeighted_TiltsTowardHeavyCandle()
    {
        // tp 10 @ vol 1 then tp 20 @ vol 3 ⇒ (10·1 + 20·3) / 4 = 17.5.
        var candles = new List<Candle>
        {
            Make(0, 11, 9, 10, 1),
            Make(1, 21, 19, 20, 3),
        };
        var vwap = VwapCalculator.Vwap(candles);

        Assert.Equal(2, vwap.Count);
        Assert.Equal(10.0, vwap[0].Value, precision: 10);
        Assert.Equal(17.5, vwap[1].Value, precision: 10);
    }

    [Fact]
    public void Vwap_SingleCandle_IsItsTypicalPrice()
    {
        var vwap = VwapCalculator.Vwap(new List<Candle> { Make(0, 15, 9, 12, 100) });

        var p = Assert.Single(vwap);
        Assert.Equal(Tp(15, 9, 12), p.Value, precision: 10);
        Assert.Equal(T0, p.Time);
    }

    [Fact]
    public void Vwap_ZeroVolumePrefix_FallsBackToTypicalPrice()
    {
        var candles = new List<Candle>
        {
            Make(0, 11, 9, 10, 0),   // no trades yet ⇒ tp fallback
            Make(1, 21, 19, 20, 4),  // first real volume takes over
        };
        var vwap = VwapCalculator.Vwap(candles);

        Assert.Equal(Tp(11, 9, 10), vwap[0].Value, precision: 10);
        Assert.Equal(20.0, vwap[1].Value, precision: 10);
    }

    [Fact]
    public void Vwap_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(VwapCalculator.Vwap(new List<Candle>()));
        Assert.Empty(VwapCalculator.Vwap(null!));
    }
}
