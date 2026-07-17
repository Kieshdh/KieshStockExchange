using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>Timestamped RSI sample (0..100).</summary>
public readonly record struct RsiPoint(DateTime Time, double Value);

/// <summary>
/// Pure function that turns a candle buffer into Wilder's RSI data points.
/// Operates on the full buffer so warmup happens off-screen; the drawable
/// filters to viewport.
/// </summary>
public static class RsiCalculator
{
    /// <summary>Wilder's RSI over close-to-close deltas: seed averages are the
    /// simple mean of the first <paramref name="period"/> gains/losses, then
    /// Wilder smoothing (period-1 weighted). First sample lands at source
    /// index <paramref name="period"/> (one delta per candle pair).</summary>
    public static IReadOnlyList<RsiPoint> Rsi(IReadOnlyList<Candle> source, int period = 14)
    {
        if (source == null || period <= 0) return Array.Empty<RsiPoint>();
        if (source.Count <= period) return Array.Empty<RsiPoint>();

        var result = new List<RsiPoint>(source.Count - period);

        double avgGain = 0.0, avgLoss = 0.0;
        for (int i = 1; i <= period; i++)
        {
            double delta = (double)source[i].Close - (double)source[i - 1].Close;
            if (delta >= 0) avgGain += delta; else avgLoss -= delta;
        }
        avgGain /= period;
        avgLoss /= period;
        result.Add(new RsiPoint(source[period].OpenTime, ToRsi(avgGain, avgLoss)));

        for (int i = period + 1; i < source.Count; i++)
        {
            double delta = (double)source[i].Close - (double)source[i - 1].Close;
            double gain = delta > 0 ? delta : 0.0;
            double loss = delta < 0 ? -delta : 0.0;
            avgGain = (avgGain * (period - 1) + gain) / period;
            avgLoss = (avgLoss * (period - 1) + loss) / period;
            result.Add(new RsiPoint(source[i].OpenTime, ToRsi(avgGain, avgLoss)));
        }
        return result;
    }

    // avgLoss==0 would divide by zero in RS; by convention pure gains read 100
    // and a perfectly flat window reads neutral 50.
    private static double ToRsi(double avgGain, double avgLoss)
    {
        if (avgLoss <= 0.0) return avgGain <= 0.0 ? 50.0 : 100.0;
        double rs = avgGain / avgLoss;
        return 100.0 - 100.0 / (1.0 + rs);
    }
}
