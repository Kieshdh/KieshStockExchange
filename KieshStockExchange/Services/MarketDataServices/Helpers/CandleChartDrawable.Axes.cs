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
    // Price <-> normalized-fraction helpers now live on ChartGeometry (mode-parameterized statics)
    // so the per-paint frame and the axes share one definition; hit-testing inverts the same math.

    private void DrawYGridAndLabels(ICanvas canvas, RectF plot, double yMin, double yMax, CurrencyType cur)
    {
        var (niceMin, niceMax, step) = ChartGeometry.NiceRange(yMin, yMax, maxTicks: 6);
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;

        // Percent scale labels tick levels as % change from the leftmost visible bar.
        double? pctRef = ScaleMode == PriceScaleMode.Percent && Candles.Count > 0
            ? (double)Candles[0].Close : (double?)null;

        for (double v = niceMin; v <= niceMax + 1e-9; v += step)
        {
            float y = plot.Bottom - (float)(ChartGeometry.PriceToFrac(v, yMin, yMax, ScaleMode) * plot.Height);
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
        TimeSpan step = ChartGeometry.ChooseTimeStep(tMin, tMax, targetTicks: 7);
        var first = ChartGeometry.AlignToStep(tMin, step, forward: true);

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
}
