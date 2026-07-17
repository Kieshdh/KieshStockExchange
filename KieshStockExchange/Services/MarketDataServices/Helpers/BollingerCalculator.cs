using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>Timestamped Bollinger sample: SMA middle band ± k·stdev envelope.</summary>
public readonly record struct BollingerPoint(DateTime Time, double Middle, double Upper, double Lower);

/// <summary>
/// Pure function that turns a candle buffer into Bollinger Band data points.
/// Operates on the full buffer so warmup happens off-screen; the drawable
/// filters to viewport.
/// </summary>
public static class BollingerCalculator
{
    /// <summary>Middle = SMA of <paramref name="period"/> closes; bands at
    /// ±<paramref name="k"/> population standard deviations (the classic
    /// Bollinger definition — divide by N, not N-1). First sample lands at
    /// source index <paramref name="period"/>-1.</summary>
    public static IReadOnlyList<BollingerPoint> Bollinger(IReadOnlyList<Candle> source, int period = 20, double k = 2.0)
    {
        if (source == null || source.Count == 0 || period <= 0) return Array.Empty<BollingerPoint>();
        if (source.Count < period) return Array.Empty<BollingerPoint>();

        var result = new List<BollingerPoint>(source.Count - period + 1);

        // Rolling Σx and Σx² keep the sweep O(n); variance = E[x²] − mean².
        double sum = 0.0, sumSq = 0.0;
        for (int i = 0; i < period; i++)
        {
            double c = (double)source[i].Close;
            sum += c; sumSq += c * c;
        }
        result.Add(MakePoint(source[period - 1].OpenTime, sum, sumSq, period, k));

        for (int i = period; i < source.Count; i++)
        {
            double added = (double)source[i].Close;
            double dropped = (double)source[i - period].Close;
            sum += added - dropped;
            sumSq += added * added - dropped * dropped;
            result.Add(MakePoint(source[i].OpenTime, sum, sumSq, period, k));
        }
        return result;
    }

    private static BollingerPoint MakePoint(DateTime time, double sum, double sumSq, int period, double k)
    {
        double mean = sum / period;
        // Clamp: floating-point cancellation can leave a tiny negative on flat windows.
        double variance = Math.Max(0.0, sumSq / period - mean * mean);
        double band = k * Math.Sqrt(variance);
        return new BollingerPoint(time, mean, mean + band, mean - band);
    }
}
