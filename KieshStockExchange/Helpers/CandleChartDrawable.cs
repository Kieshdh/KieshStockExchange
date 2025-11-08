using System.Globalization;
using KieshStockExchange.Models;

namespace KieshStockExchange.Helpers;

public sealed class CandleChartDrawable : IDrawable
{
    #region Properties
    public IReadOnlyList<Candle> Candles { get; set; } = Array.Empty<Candle>();
    public double YPaddingPercent { get; set; } = 0.06;
    public double XPaddingPercent { get; set; } = 0.06;

    // Style
    public Color Bg = Colors.Transparent;               // background
    public Color Axis = Color.FromArgb("#556b7a");      // axis text & ticks
    public Color Grid = Color.FromArgb("#203a4a");      // gridlines (faint)
    public Color Bull = Color.FromArgb("#2ecc71");      // green
    public Color Bear = Color.FromArgb("#e74c3c");      // red
    public Color Wick = Color.FromArgb("#99b2c2");      // Candle wick
    public float AxisFont = 10f;

    // Layout paddings
    const float LeftAxisW = 56f;    // reserve space for Y labels
    const float BottomAxisH = 24f;  // reserve space for time labels
    const float TopPad = 6f;    
    const float RightPad = 8f;
    #endregion

    #region IDrawable Implementation
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.SaveState();
        canvas.FillColor = Bg;
        canvas.FillRectangle(dirtyRect);

        // No data
        if (Candles.Count < 1)
        {
            DrawNoData(canvas, dirtyRect);
            canvas.RestoreState();
            return;
        }

        // Plot rectangle inside the axes
        var plot = new RectF(dirtyRect.X + LeftAxisW,
                             dirtyRect.Y + TopPad,
                             Math.Max(1f, dirtyRect.Width - LeftAxisW - RightPad),
                             Math.Max(1f, dirtyRect.Height - BottomAxisH - TopPad));

        // Visible time-range
        var tMin = Candles.First().OpenTime; // start-of-first bucket
        var tMax = Candles.Last().CloseTime; // End-of-last bucket
        if (tMax <= tMin) tMax = tMin.AddSeconds(1); // avoid division-by-zero

        // Add padding to the right and calculate span seconds
        var xPad = TimeSpan.FromTicks((long)((tMax - tMin).Ticks * XPaddingPercent));
        tMax += xPad;
        double spanSec = (tMax - tMin).TotalSeconds;

        // Visible price-range
        decimal low = Candles.Min(c => c.Low);
        decimal high = Candles.Max(c => c.High);
        if (high <= low) { high = low + 1m; } // avoid flat zero range

        // Y padding
        var yPad = (double)(high - low) * Math.Max(0, YPaddingPercent);
        double yMin = (double)low - yPad;
        double yMax = (double)high + yPad;
        if (yMax <= yMin) yMax = yMin + 1.0;

        // Coordinate transforms
        float X(DateTime utc) => plot.Left + (float)(((utc - tMin).TotalSeconds / spanSec) * plot.Width);
        float Y(double price) => plot.Bottom - (float)(((price - yMin) / (yMax - yMin)) * plot.Height);

        // Grid & axes
        DrawYGridAndLabels(canvas, plot, yMin, yMax, Candles[0].CurrencyType);
        DrawXGridAndLabels(canvas, plot, tMin, tMax);

        // Candlesticks
        DrawCandles(canvas, plot, X, Y);

        // Border
        canvas.StrokeColor = Axis;
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

    private void DrawYGridAndLabels(ICanvas canvas, RectF plot, double yMin, double yMax, CurrencyType cur)
    {
        var (niceMin, niceMax, step) = NiceRange(yMin, yMax, maxTicks: 6);
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;

        for (double v = niceMin; v <= niceMax + 1e-9; v += step)
        {
            float y = plot.Bottom - (float)((v - yMin) / (yMax - yMin) * plot.Height);
            // gridline
            canvas.StrokeColor = Grid; canvas.StrokeSize = 1f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);

            // label
            var label = CurrencyHelper.Format((decimal)v, cur);
            // draw right-aligned into the left axis gutter
            canvas.DrawString(label, new RectF(0, y - 7, plot.Left - 6, 14),
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }
    }

    private void DrawXGridAndLabels(ICanvas canvas, RectF plot, DateTime tMin, DateTime tMax)
    {
        // pick a time step that yields ~6–8 ticks
        TimeSpan step = ChooseTimeStep(tMin, tMax, targetTicks: 7);
        var first = AlignToStep(tMin, step, forward: true);

        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;

        double total = (tMax - tMin).TotalSeconds;

        for (var t = first; t <= tMax; t = t.Add(step))
        {
            float x = plot.Left + (float)(((t - tMin).TotalSeconds / total) * plot.Width);
            canvas.DrawLine(x, plot.Top, x, plot.Bottom); // vertical grid

            // label: if midnight, show date; else show time HH:mm
            string text = (t.TimeOfDay == TimeSpan.Zero)
                ? t.ToLocalTime().ToString("ddd dd MMM", CultureInfo.InvariantCulture)
                : t.ToLocalTime().ToString("HH:mm");

            canvas.DrawString(text, new RectF(x - 40, plot.Bottom + 2, 80, BottomAxisH - 2),
                HorizontalAlignment.Center, VerticalAlignment.Top);
        }
    }
    #endregion

    #region Private Methods
    private void DrawCandles(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y)
    {
        // approximate candle body width by bucket time (Open→Close)
        // If buckets vary, we still compute per candle using Open/Close times.
        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i]; // Current candle
            float xOpen = X(c.OpenTime); // Left body edge
            float xClose = X(c.CloseTime); // Right body edge
            float cx = (xOpen + xClose) * 0.5f; // Center x coordinate
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen ) * 0.7f); // Body width (70% of full)

            float yOpen = Y((double)c.Open);
            float yClose = Y((double)c.Close);
            float yHigh = Y((double)c.High);
            float yLow = Y((double)c.Low);

            bool bull = c.Close >= c.Open;
            var bodyColor = bull ? Bull : Bear;

            // wick
            canvas.StrokeColor = Wick;
            canvas.StrokeSize = 1.2f;
            canvas.DrawLine(cx, yHigh, cx, yLow);

            // body (rectangle)
            float top = Math.Min(yOpen, yClose);
            float h = Math.Max(1f, Math.Abs(yClose - yOpen));
            canvas.FillColor = bodyColor;
            canvas.FillRectangle(cx - bodyW / 2f, top, bodyW, h);
            // outline
            canvas.StrokeColor = bodyColor.WithAlpha(0.95f);
            canvas.StrokeSize = 1f;
            canvas.DrawRectangle(cx - bodyW / 2f, top, bodyW, h);
        }
    }

    private static (double niceMin, double niceMax, double step) NiceRange(double min, double max, int maxTicks)
    {
        // classic “nice numbers” algorithm
        var range = NiceNum(max - min, round: false);
        var step = NiceNum(range / (maxTicks - 1), round: true);
        var niceMin = Math.Floor(min / step) * step;
        var niceMax = Math.Ceiling(max / step) * step;
        return (niceMin, niceMax, step);

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

        // pick the step that yields ~ targetTicks
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

    private static DateTime AlignToStep(DateTime t, TimeSpan step, bool forward)
    {
        // Align to next/prev boundary in UTC (floor or ceil)
        var ticks = step.Ticks;
        long k = t.Ticks / ticks;
        long aligned = forward
            ? ((t.Ticks % ticks) == 0 ? t.Ticks : (k + 1) * ticks)
            : k * ticks;
        return new DateTime(aligned, DateTimeKind.Utc);
    }
    #endregion
}
