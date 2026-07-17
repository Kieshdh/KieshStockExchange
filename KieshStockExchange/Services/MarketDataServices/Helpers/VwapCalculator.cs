using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers;

/// <summary>Timestamped cumulative-VWAP sample.</summary>
public readonly record struct VwapPoint(DateTime Time, double Value);

/// <summary>
/// Pure function that turns a candle buffer into cumulative volume-weighted
/// average price points: running Σ(typicalPrice·volume) / Σ(volume) with
/// typical price (H+L+C)/3, one point per candle.
/// Session reset (intraday VWAP restarting each trading session) is a
/// rendering/wiring concern for later — this operates on whatever slice it's given.
/// </summary>
public static class VwapCalculator
{
    public static IReadOnlyList<VwapPoint> Vwap(IReadOnlyList<Candle> source)
    {
        if (source == null || source.Count == 0) return Array.Empty<VwapPoint>();

        var result = new List<VwapPoint>(source.Count);
        double cumNotional = 0.0, cumVolume = 0.0;

        for (int i = 0; i < source.Count; i++)
        {
            var c = source[i];
            double tp = ((double)c.High + (double)c.Low + (double)c.Close) / 3.0;
            cumNotional += tp * c.Volume;
            cumVolume += c.Volume;
            // No volume yet ⇒ ratio undefined; the typical price is the least-surprising stand-in.
            double vwap = cumVolume > 0.0 ? cumNotional / cumVolume : tp;
            result.Add(new VwapPoint(c.OpenTime, vwap));
        }
        return result;
    }
}
