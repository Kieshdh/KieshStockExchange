using System.Globalization;
using KieshStockExchange.Models;
using KieshStockExchange.Models.ChartDrawing.Objects;
using KieshStockExchange.Models.ChartDrawing.Style;
using KieshStockExchange.Models.ChartDrawing.Tools;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers;
using KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed partial class CandleChartDrawable
{
    #region Axes and Grid
    // Price <-> normalized vertical fraction (0 = plot bottom, 1 = plot top), keyed on ScaleMode.
    // Log mode maps equal RATIOS to equal pixels; Linear/Percent are plain linear. PixelToPrice
    // inverts this so hit-testing stays exact under every scale.
    private double PriceToFrac(double price, double lo, double hi)
    {
        if (ScaleMode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return b <= a ? 0.0 : (Math.Log(Math.Max(price, 1e-9)) - a) / (b - a);
        }
        return hi <= lo ? 0.0 : (price - lo) / (hi - lo);
    }

    private double FracToPrice(double frac, double lo, double hi)
    {
        if (ScaleMode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return Math.Exp(a + frac * (b - a));
        }
        return lo + frac * (hi - lo);
    }

    private void DrawYGridAndLabels(ICanvas canvas, RectF plot, double yMin, double yMax, CurrencyType cur)
    {
        var (niceMin, niceMax, step) = NiceRange(yMin, yMax, maxTicks: 6);
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;

        // Percent scale labels tick levels as % change from the leftmost visible bar.
        double? pctRef = ScaleMode == PriceScaleMode.Percent && Candles.Count > 0
            ? (double)Candles[0].Close : (double?)null;

        for (double v = niceMin; v <= niceMax + 1e-9; v += step)
        {
            float y = plot.Bottom - (float)(PriceToFrac(v, yMin, yMax) * plot.Height);
            if (y < plot.Top - 1 || y > plot.Bottom + 1) continue;

            // Horizontal grid line
            canvas.StrokeColor = Grid; canvas.StrokeSize = 1f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);

            // Label in the right gutter, aligned to the gridline.
            string label;
            if (pctRef is double r && r > 0)
            {
                double pc = (v / r - 1.0) * 100.0;
                label = $"{(pc >= 0 ? "+" : "")}{pc:0.0}%";
            }
            else label = CurrencyHelper.Format((decimal)v, cur);

            canvas.DrawString(label,
                new RectF(plot.Right + 4, y - 7, RightAxisW - 8, 14),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    private void DrawXGridAndLabels(ICanvas canvas, RectF plot, DateTime tMin, DateTime tMax)
    {
        TimeSpan step = ChooseTimeStep(tMin, tMax, targetTicks: 7);
        var first = AlignToStep(tMin, step, forward: true);

        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;

        double total = (tMax - tMin).TotalSeconds;
        if (total <= 0) return;

        for (var t = first; t <= tMax; t = t.Add(step))
        {
            float x = plot.Left + (float)(((t - tMin).TotalSeconds / total) * plot.Width);

            // Vertical grid line
            canvas.DrawLine(x, plot.Top, x, plot.Bottom);

            // Show full date when crossing midnight, otherwise just time
            string text = (t.TimeOfDay == TimeSpan.Zero)
                ? t.ToLocalTime().ToString("ddd dd MMM", CultureInfo.InvariantCulture)
                : t.ToLocalTime().ToString("HH:mm");

            canvas.DrawString(text, new RectF(x - 40, plot.Bottom + 2, 80, BottomAxisH - 2),
                HorizontalAlignment.Center, VerticalAlignment.Top);
        }
    }
    #endregion

    // Picks the time-step from a fixed candidate list that produces the closest number of ticks to targetTicks.
    private static TimeSpan ChooseTimeStep(DateTime from, DateTime to, int targetTicks)
    {
        var total = to - from;
        var candidates = new[]
        {
            TimeSpan.FromSeconds(1),  TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),  TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
            TimeSpan.FromHours(2),    TimeSpan.FromHours(4),
            TimeSpan.FromHours(6),    TimeSpan.FromHours(12),
            TimeSpan.FromDays(1),     TimeSpan.FromDays(7)
        };

        TimeSpan best = candidates[0];
        double bestDiff = double.MaxValue;
        foreach (var c in candidates)
        {
            var ticks = Math.Max(1, (int)Math.Round(total.TotalSeconds / c.TotalSeconds));
            var diff = Math.Abs(ticks - targetTicks);
            if (diff < bestDiff) { bestDiff = diff; best = c; }
        }
        return best;
    }

    // Snaps t to the nearest multiple of step, rounding forward or backward.
    private static DateTime AlignToStep(DateTime t, TimeSpan step, bool forward)
    {
        var ticks = step.Ticks;
        long k = t.Ticks / ticks;
        long aligned = forward
            ? ((t.Ticks % ticks) == 0 ? t.Ticks : (k + 1) * ticks)
            : k * ticks;
        return new DateTime(aligned, DateTimeKind.Utc);
    }

    // Returns a human-friendly axis range and tick step that neatly covers [min, max].
    private static (double niceMin, double niceMax, double step) NiceRange(double min, double max, int maxTicks)
    {
        var range = NiceNum(max - min, round: false);
        var step = NiceNum(range / (maxTicks - 1), round: true);
        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;
        return (niceMin, niceMax, step);

        // Rounds x to a "nice" number (1, 2, 5, 10 …) — classic Wilkinson algorithm.
        static double NiceNum(double x, bool round)
        {
            var exp = Math.Floor(Math.Log10(x));
            var f = x / Math.Pow(10, exp);
            double nf;
            if (round)
            {
                if (f < 1.5) nf = 1;
                else if (f < 3) nf = 2;
                else if (f < 7) nf = 5;
                else nf = 10;
            }
            else
            {
                if (f <= 1) nf = 1;
                else if (f <= 2) nf = 2;
                else if (f <= 5) nf = 5;
                else nf = 10;
            }
            return nf * Math.Pow(10, exp);
        }
    }
}
