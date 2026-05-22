using Microsoft.Maui.Graphics;

namespace KieshStockExchange.Views.AdminPageViews;

/// <summary>
/// Bot activity chart drawable. Renders a single time series with a y-axis
/// (4 evenly-spaced labels) and an x-axis (5 time labels). Used by the
/// BotDashboard activity card; the host page swaps Values + Invalidates when
/// the user picks a different metric or new buckets arrive.
/// </summary>
public sealed class BotSparklineDrawable : IDrawable
{
    private const float LeftPad = 56f;   // room for y-axis labels
    private const float RightPad = 6f;
    private const float TopPad = 6f;
    private const float BottomPad = 18f; // room for x-axis labels

    public IReadOnlyList<double> Values { get; set; } = Array.Empty<double>();
    public Color LineColor { get; set; } = Colors.SteelBlue;
    public Color AxisColor { get; set; } = Color.FromArgb("#707078");
    public Color GridColor { get; set; } = Color.FromArgb("#2B2B33");
    public float LineThickness { get; set; } = 1.5f;
    public float FillAlpha { get; set; } = 0.18f;

    public Func<double, string> ValueFormatter { get; set; } = v => v.ToString("F0");
    public DateTime EndTime { get; set; } = DateTime.Now;
    public TimeSpan TimeRange { get; set; } = TimeSpan.FromHours(1);

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float w = dirtyRect.Width;
        float h = dirtyRect.Height;
        if (w <= 0 || h <= 0) return;

        float plotLeft = LeftPad;
        float plotRight = w - RightPad;
        float plotTop = TopPad;
        float plotBottom = h - BottomPad;
        float plotW = plotRight - plotLeft;
        float plotH = plotBottom - plotTop;
        if (plotW <= 0 || plotH <= 0) return;

        // Y-axis scale: pick a "nice" max so the labels read in round steps.
        double rawMax = 0;
        for (int i = 0; i < Values.Count; i++) if (Values[i] > rawMax) rawMax = Values[i];
        double niceMax = NiceMax(rawMax);
        if (niceMax <= 0) niceMax = 1;

        // Grid lines + y-axis labels (5 ticks: 0, ¼, ½, ¾, max).
        canvas.FontColor = AxisColor;
        canvas.FontSize = 10;
        canvas.StrokeSize = 1;
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            float y = plotBottom - t * plotH;
            canvas.StrokeColor = GridColor;
            canvas.DrawLine(plotLeft, y, plotRight, y);

            var label = ValueFormatter(niceMax * t);
            canvas.DrawString(label, 0, y - 6, plotLeft - 4, 12,
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }

        // X-axis time labels (5 labels: start, ¼, ½, ¾, end).
        var start = EndTime - TimeRange;
        string fmt = TimeRange <= TimeSpan.FromMinutes(15) ? "HH:mm:ss" : "HH:mm";
        for (int i = 0; i <= 4; i++)
        {
            float t = i / 4f;
            float x = plotLeft + t * plotW;
            var ts = start + TimeSpan.FromTicks((long)(TimeRange.Ticks * t));
            var halign = i == 0 ? HorizontalAlignment.Left
                       : i == 4 ? HorizontalAlignment.Right
                                : HorizontalAlignment.Center;
            float lx = i == 0 ? x : i == 4 ? x - 60 : x - 30;
            canvas.DrawString(ts.ToString(fmt), lx, plotBottom + 2, 60, 14,
                halign, VerticalAlignment.Top);
        }

        if (Values.Count < 2) return;

        float dx = plotW / (Values.Count - 1);
        var line = new PathF();
        for (int i = 0; i < Values.Count; i++)
        {
            float x = plotLeft + i * dx;
            float y = (float)(plotBottom - (Values[i] / niceMax) * plotH);
            if (i == 0) line.MoveTo(x, y);
            else line.LineTo(x, y);
        }

        var fill = new PathF(line);
        fill.LineTo(plotRight, plotBottom);
        fill.LineTo(plotLeft, plotBottom);
        fill.Close();
        canvas.FillColor = LineColor.WithAlpha(FillAlpha);
        canvas.FillPath(fill);

        canvas.StrokeColor = LineColor;
        canvas.StrokeSize = LineThickness;
        canvas.DrawPath(line);
    }

    // Round up to a "nice" upper bound (1, 2, 5 × 10^n) so the labels read
    // as round numbers. Mirrors the standard chart-axis algorithm.
    private static double NiceMax(double value)
    {
        if (value <= 0) return 0;
        var exponent = Math.Floor(Math.Log10(value));
        var fraction = value / Math.Pow(10, exponent);
        double niceFraction;
        if (fraction <= 1) niceFraction = 1;
        else if (fraction <= 2) niceFraction = 2;
        else if (fraction <= 5) niceFraction = 5;
        else niceFraction = 10;
        return niceFraction * Math.Pow(10, exponent);
    }
}
