using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>
/// Pure functions that turn a candle buffer into a sequence of moving-average
/// data points. Operate on the full buffer so values at the left edge of the
/// chart's viewport are correct after warmup; the drawable filters to viewport.
/// </summary>
public static class MovingAverageCalculator
{
    /// <summary>Simple Moving Average of <paramref name="period"/> closes.</summary>
    public static IReadOnlyList<MaPoint> Sma(IReadOnlyList<Candle> source, int period)
    {
        if (source == null || source.Count == 0 || period <= 0) return Array.Empty<MaPoint>();
        if (source.Count < period) return Array.Empty<MaPoint>();

        var result = new List<MaPoint>(source.Count - period + 1);
        double sum = 0.0;
        for (int i = 0; i < period; i++) sum += (double)source[i].Close;
        result.Add(new MaPoint(source[period - 1].OpenTime, sum / period));

        for (int i = period; i < source.Count; i++)
        {
            sum += (double)source[i].Close - (double)source[i - period].Close;
            result.Add(new MaPoint(source[i].OpenTime, sum / period));
        }
        return result;
    }

    /// <summary>Exponential Moving Average. First sample is the SMA of the
    /// initial <paramref name="period"/> closes; subsequent samples use the
    /// standard 2/(period+1) smoothing factor.</summary>
    public static IReadOnlyList<MaPoint> Ema(IReadOnlyList<Candle> source, int period)
    {
        if (source == null || source.Count == 0 || period <= 0) return Array.Empty<MaPoint>();
        if (source.Count < period) return Array.Empty<MaPoint>();

        var result = new List<MaPoint>(source.Count - period + 1);
        double k = 2.0 / (period + 1);

        double sum = 0.0;
        for (int i = 0; i < period; i++) sum += (double)source[i].Close;
        double ema = sum / period;
        result.Add(new MaPoint(source[period - 1].OpenTime, ema));

        for (int i = period; i < source.Count; i++)
        {
            ema = ((double)source[i].Close - ema) * k + ema;
            result.Add(new MaPoint(source[i].OpenTime, ema));
        }
        return result;
    }
}
