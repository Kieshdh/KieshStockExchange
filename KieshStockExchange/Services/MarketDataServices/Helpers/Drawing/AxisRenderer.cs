using System.Globalization;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Price/time grid + axis labels. Stateless renderer collaborator: geometry and ranges arrive in
// the RenderFrame, colours/fonts in the ChartTheme, and the candle series per call (Percent-mode
// labels reference the leftmost visible close) — no reference back to CandleChartDrawable.
// Gutter sizes are the spine's layout consts, injected once at construction (the
// CrosshairRenderer pattern). Tick math lives on ChartGeometry.
internal sealed class AxisRenderer
{
    private readonly float RightAxisW;
    private readonly float BottomAxisH;

    public AxisRenderer(float rightAxisW, float bottomAxisH)
    {
        RightAxisW = rightAxisW;
        BottomAxisH = bottomAxisH;
    }

    public void DrawYGridAndLabels(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<Candle> candles)
    {
        var plot = f.Plot;
        var (niceMin, niceMax, step) = ChartGeometry.NiceRange(f.YMin, f.YMax, maxTicks: 6);
        canvas.FontColor = t.Axis;
        canvas.FontSize = t.AxisFont;

        // Percent scale labels tick levels as % change from the leftmost visible bar.
        double? pctRef = f.Mode == PriceScaleMode.Percent && candles.Count > 0
            ? (double)candles[0].Close : (double?)null;

        for (double v = niceMin; v <= niceMax + 1e-9; v += step)
        {
            float y = plot.Bottom - (float)(ChartGeometry.PriceToFrac(v, f.YMin, f.YMax, f.Mode) * plot.Height);
            if (y < plot.Top - 1 || y > plot.Bottom + 1) continue;

            // Horizontal grid line
            canvas.StrokeColor = t.Grid; canvas.StrokeSize = 1f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);

            // Label in the right gutter, aligned to the gridline.
            string label;
            if (pctRef is double r && r > 0)
            {
                double pc = (v / r - 1.0) * 100.0;
                label = $"{(pc >= 0 ? "+" : "")}{pc:0.0}%";
            }
            else label = CurrencyHelper.Format((decimal)v, f.Currency);

            canvas.DrawString(label,
                new RectF(plot.Right + 4, y - 7, RightAxisW - 8, 14),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    public void DrawXGridAndLabels(ICanvas canvas, RenderFrame f, ChartTheme t)
    {
        var plot = f.Plot;
        DateTime tMin = f.TMin, tMax = f.TMax;
        TimeSpan step = ChartGeometry.ChooseTimeStep(tMin, tMax, targetTicks: 7);
        var first = ChartGeometry.AlignToStep(tMin, step, forward: true);

        canvas.FontColor = t.Axis;
        canvas.FontSize = t.AxisFont;
        canvas.StrokeColor = t.Grid;
        canvas.StrokeSize = 1f;

        double total = (tMax - tMin).TotalSeconds;
        if (total <= 0) return;

        for (var tick = first; tick <= tMax; tick = tick.Add(step))
        {
            float x = plot.Left + (float)(((tick - tMin).TotalSeconds / total) * plot.Width);

            // Vertical grid line
            canvas.DrawLine(x, plot.Top, x, plot.Bottom);

            // Show full date when crossing midnight, otherwise just time
            string text = (tick.TimeOfDay == TimeSpan.Zero)
                ? tick.ToLocalTime().ToString("ddd dd MMM", CultureInfo.InvariantCulture)
                : tick.ToLocalTime().ToString("HH:mm");

            canvas.DrawString(text, new RectF(x - 40, plot.Bottom + 2, 80, BottomAxisH - 2),
                HorizontalAlignment.Center, VerticalAlignment.Top);
        }
    }
}
