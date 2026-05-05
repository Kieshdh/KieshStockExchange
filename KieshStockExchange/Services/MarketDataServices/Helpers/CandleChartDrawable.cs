using System.Globalization;
using KieshStockExchange.Models;
using KieshStockExchange.Helpers;
using KieshStockExchange.Services.MarketDataServices.Interfaces;

namespace KieshStockExchange.Services.MarketDataServices;

public sealed class CandleChartDrawable : IDrawable
{
    #region Properties
    public IReadOnlyList<Candle> Candles { get; set; } = Array.Empty<Candle>();
    public double YPaddingPercent { get; set; } = 0.06;
    public double XPaddingPercent { get; set; } = 0.02;

    // Current live price; when set, drawn as a horizontal price line and tag in the right gutter.
    public decimal? CurrentPrice { get; set; }

    // Palette â€” populated by ChartView at construction time. Defaults are intentionally stark so a
    // missing resource is obvious rather than silently themed.
    public Color Bg = Colors.Black;
    public Color Axis = Colors.Gray;
    public Color Grid = Colors.DimGray;
    public Color Bull = Colors.Green;
    public Color Bear = Colors.Red;
    public Color PriceLineUp = Colors.Green;
    public Color PriceLineDown = Colors.Red;

    public float AxisFont = 10f;
    public float PriceTagFont = 10f;

    // Layout paddings â€” leave gutters for the Y labels (right) and time labels (bottom).
    const float RightAxisW = 64f;   // reserve space for Y labels
    const float BottomAxisH = 24f;  // reserve space for time labels
    const float TopPad = 6f;
    const float LeftPad = 6f;
    #endregion

    #region IDrawable Implementation
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();
        canvas.FillColor = Bg;
        canvas.FillRectangle(dirtyRect);

        if (Candles.Count < 1)
        {
            DrawNoData(canvas, dirtyRect);
            canvas.RestoreState();
            return;
        }

        // Plot rectangle inside the axes
        var plot = new RectF(dirtyRect.X + LeftPad,
                             dirtyRect.Y + TopPad,
                             Math.Max(1f, dirtyRect.Width - RightAxisW - LeftPad),
                             Math.Max(1f, dirtyRect.Height - BottomAxisH - TopPad));

        // Visible time-range
        var tMin = Candles.First().OpenTime;
        var tMax = Candles.Last().CloseTime;
        if (tMax <= tMin) tMax = tMin.AddSeconds(1);

        // Right-side X padding so the latest candle isn't flush against the axis
        var xPad = TimeSpan.FromTicks((long)((tMax - tMin).Ticks * Math.Max(0, XPaddingPercent)));
        tMax += xPad;
        double spanSec = (tMax - tMin).TotalSeconds;

        // Visible price-range across the candle window.
        decimal low = Candles[0].Low;
        decimal high = Candles[0].High;
        for (int i = 1; i < Candles.Count; i++)
        {
            var c = Candles[i];
            if (c.Low < low) low = c.Low;
            if (c.High > high) high = c.High;
        }
        // Ensure the live price is always inside the visible range
        if (CurrentPrice is decimal cp)
        {
            if (cp < low) low = cp;
            if (cp > high) high = cp;
        }
        if (high <= low) high = low + 1m;

        // Add top/bottom padding so candles don't hug the plot edges
        var yPad = (double)(high - low) * Math.Max(0, YPaddingPercent);
        double yMin = (double)low - yPad;
        double yMax = (double)high + yPad;
        if (yMax <= yMin) yMax = yMin + 1.0;

        // Coordinate transforms from data-space to plot-space.
        float X(DateTime utc) => plot.Left + (float)(((utc - tMin).TotalSeconds / spanSec) * plot.Width);
        float Y(double price) => plot.Bottom - (float)(((price - yMin) / (yMax - yMin)) * plot.Height);

        var currency = Candles[0].CurrencyType;

        DrawYGridAndLabels(canvas, plot, yMin, yMax, currency);
        DrawXGridAndLabels(canvas, plot, tMin, tMax);
        DrawCandles(canvas, plot, X, Y);
        DrawCurrentPriceLine(canvas, plot, Y, currency);

        // Border around the plot area.
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(plot);

        canvas.RestoreState();
    }

    private void DrawNoData(ICanvas canvas, RectF r)
    {
        canvas.FontSize = 12f;
        canvas.FontColor = Axis;
        canvas.DrawString("No data", r, HorizontalAlignment.Center, VerticalAlignment.Center);
    }
    #endregion

    #region Axes and Grid
    private void DrawYGridAndLabels(ICanvas canvas, RectF plot, double yMin, double yMax, CurrencyType cur)
    {
        var (niceMin, niceMax, step) = NiceRange(yMin, yMax, maxTicks: 6);
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;

        for (double v = niceMin; v <= niceMax + 1e-9; v += step)
        {
            float y = plot.Bottom - (float)((v - yMin) / (yMax - yMin) * plot.Height);

            // Horizontal grid line
            canvas.StrokeColor = Grid; canvas.StrokeSize = 1f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);

            // Label in the right gutter, aligned to the gridline.
            var label = CurrencyHelper.Format((decimal)v, cur);
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

    #region Candle Drawing
    private void DrawCandles(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y)
    {
        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float xOpen = X(c.OpenTime);
            float xClose = X(c.CloseTime);
            float cx = (xOpen + xClose) * 0.5f;
            // Body takes 70% of the candle slot, leaving gaps between adjacent bars.
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen) * 0.7f);

            float yOpen = Y((double)c.Open);
            float yClose = Y((double)c.Close);
            float yHigh = Y((double)c.High);
            float yLow = Y((double)c.Low);

            bool bull = c.Close >= c.Open;
            var bodyColor = bull ? Bull : Bear;

            // Wick takes the body colour so the candle reads as one shape.
            canvas.StrokeColor = bodyColor;
            canvas.StrokeSize = 1f;
            canvas.DrawLine(cx, yHigh, cx, yLow);

            // Body â€” clamp height to 1px minimum so doji candles are still visible.
            float top = Math.Min(yOpen, yClose);
            float h = Math.Max(1f, Math.Abs(yClose - yOpen));
            canvas.FillColor = bodyColor;
            canvas.FillRectangle(cx - bodyW / 2f, top, bodyW, h);
        }
    }

    private void DrawCurrentPriceLine(ICanvas canvas, RectF plot, Func<double, float> Y, CurrencyType cur)
    {
        if (CurrentPrice is not decimal price) return;
        if (Candles.Count == 0) return;

        float y = Y((double)price);
        // Skip drawing if the price falls outside the visible plot.
        if (y < plot.Top || y > plot.Bottom) return;

        // Colour matches the direction of the most recent candle.
        var last = Candles[^1];
        bool up = last.Close >= last.Open;
        var color = up ? PriceLineUp : PriceLineDown;

        // Dashed horizontal line across the plot.
        canvas.SaveState();
        canvas.StrokeColor = color;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 4f, 3f };
        canvas.DrawLine(plot.Left, y, plot.Right, y);
        canvas.StrokeDashPattern = null;
        canvas.RestoreState();

        // Price tag in the right gutter, drawn as a filled pill with white text.
        var label = CurrencyHelper.Format(price, cur);
        var tagRect = new RectF(plot.Right + 1, y - 8, RightAxisW - 2, 16);
        canvas.FillColor = color;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(label,
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
            HorizontalAlignment.Left, VerticalAlignment.Center);
    }
    #endregion

    #region Private Helpers
    // Returns a human-friendly axis range and tick step that neatly covers [min, max].
    private static (double niceMin, double niceMax, double step) NiceRange(double min, double max, int maxTicks)
    {
        var range = NiceNum(max - min, round: false);
        var step = NiceNum(range / (maxTicks - 1), round: true);
        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;
        return (niceMin, niceMax, step);

        // Rounds x to a "nice" number (1, 2, 5, 10 â€¦) â€” classic Wilkinson algorithm.
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
    #endregion
}
