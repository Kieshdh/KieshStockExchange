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
    private void DrawMovingAverages(ICanvas canvas, RectF plot,
        DateTime tMin, DateTime tMax, double yMin, double yMax,
        Func<DateTime, float> X, Func<double, float> Y)
    {
        if (MaSeries.Count == 0) return;

        // MaPoint.AtTime is OpenTime; shift to candle centre so the line tracks
        // the middle of each bucket rather than the left edge.
        var halfBucket = Viewport.IsValid
            ? TimeSpan.FromTicks(Viewport.Bucket.Ticks / 2)
            : TimeSpan.Zero;

        canvas.SaveState();
        canvas.StrokeSize = 1.5f;
        foreach (var series in MaSeries)
        {
            var pts = series.Points;
            if (pts == null || pts.Count == 0) continue;

            canvas.StrokeColor = series.Color;
            var path = new PathF();
            bool started = false;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var t = p.AtTime + halfBucket;
                if (t < tMin) continue;
                if (t > tMax) break;
                if (p.Value < yMin || p.Value > yMax) { started = false; continue; }

                float px = X(t);
                float py = Y(p.Value);
                if (!started) { path.MoveTo(px, py); started = true; }
                else path.LineTo(px, py);
            }
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    private void DrawVolume(ICanvas canvas, RectF volRect, Func<DateTime, float> X)
    {
        // Sub-pane mode draws a border so the panel reads as distinct. Overlay
        // mode shares the price plot's rectangle so a second border would just
        // cut a horizontal line across the chart.
        if (!OverlayVolume)
        {
            canvas.StrokeColor = Grid;
            canvas.StrokeSize = 1f;
            canvas.DrawRectangle(volRect);
        }

        if (Candles.Count == 0) return;

        long maxVol = 0L;
        for (int i = 0; i < Candles.Count; i++)
            if (Candles[i].Volume > maxVol) maxVol = Candles[i].Volume;
        if (maxVol <= 0) return;

        // Overlay mode lowers alpha further so candles drawn on top stay legible.
        float overlayAlpha = OverlayVolume ? 0.35f : 0.6f;
        var bullTint = VolumeBullTint.Alpha > 0 ? VolumeBullTint : Bull.WithAlpha(overlayAlpha);
        var bearTint = VolumeBearTint.Alpha > 0 ? VolumeBearTint : Bear.WithAlpha(overlayAlpha);

        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float xOpen = X(c.OpenTime);
            float xClose = X(c.CloseTime);
            float cx = (xOpen + xClose) * 0.5f;
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen) * 0.7f);

            float h = (float)(c.Volume / (double)maxVol * volRect.Height);
            if (h < 1f) h = c.Volume > 0 ? 1f : 0f;

            canvas.FillColor = c.Close >= c.Open ? bullTint : bearTint;
            canvas.FillRectangle(cx - bodyW / 2f, volRect.Bottom - h, bodyW, h);
        }
    }

    /// <summary>
    /// §market-mood: draw the accumulated Fear/Greed series in its own sub-pane on a FIXED 0..100 scale.
    /// Red band below 30 (fear) and green band above 70 (greed) wash the zones; 0/50/100 gridlines label
    /// the axis; the line is plotted against the shared X() time transform so it lines up with the candles.
    /// A current-value pill (score + label) sits top-left of the strip.
    /// </summary>
    private void DrawMood(ICanvas canvas, RectF r, Func<DateTime, float> X, DateTime tMin, DateTime tMax)
    {
        // Sub-pane border, matching the volume sub-pane.
        canvas.StrokeColor = Grid;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(r);

        float MoodY(double v) => r.Bottom - (float)(Math.Clamp(v, 0, 100) / 100.0 * r.Height);

        // Fear (<30) / greed (>70) zone washes so the strip reads at a glance.
        float y30 = MoodY(30), y70 = MoodY(70);
        canvas.FillColor = Bear.WithAlpha(0.10f);
        canvas.FillRectangle(r.X, y30, r.Width, r.Bottom - y30);
        canvas.FillColor = Bull.WithAlpha(0.10f);
        canvas.FillRectangle(r.X, r.Y, r.Width, y70 - r.Y);

        // 0 / 50 / 100 gridlines + right-gutter labels.
        canvas.FontColor = Axis;
        canvas.FontSize = AxisFont;
        foreach (var lvl in new[] { 0.0, 50.0, 100.0 })
        {
            float y = MoodY(lvl);
            canvas.StrokeColor = Grid.WithAlpha(0.5f);
            canvas.StrokeSize = 1f;
            canvas.DrawLine(r.Left, y, r.Right, y);
            canvas.DrawString($"{lvl:0}",
                new RectF(r.Right + 4, y - 7, RightAxisW - 8, 14),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        // Accumulated series polyline. Clipped to the visible time window; values map on the fixed scale.
        if (MoodSeries.Count > 0)
        {
            var path = new PathF();
            bool started = false;
            for (int i = 0; i < MoodSeries.Count; i++)
            {
                var (t, v) = MoodSeries[i];
                if (t < tMin || t > tMax) { started = false; continue; }
                float px = X(t), py = MoodY(v);
                if (!started) { path.MoveTo(px, py); started = true; }
                else path.LineTo(px, py);
            }
            canvas.StrokeColor = MoodLineColor;
            canvas.StrokeSize = 1.6f;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);

            // Current-value pill top-left: latest score + fear/greed word, tinted by zone.
            double latest = MoodSeries[^1].Value;
            var (word, tint) = latest >= 70 ? ("Greed", Bull)
                             : latest <= 30 ? ("Fear", Bear)
                             : ("Neutral", MoodLineColor);
            string label = $"Mood {latest:0}  {word}";
            var pill = new RectF(r.Left + 4, r.Top + 3, Math.Max(96f, label.Length * 6.5f), 15f);
            canvas.FillColor = tint.WithAlpha(0.85f);
            canvas.FillRectangle(pill);
            canvas.FontColor = Colors.White;
            canvas.FontSize = AxisFont;
            canvas.DrawString(label, new RectF(pill.X + 4, pill.Y, pill.Width - 6, pill.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    /// <summary>
    /// §depth-overlay: draw the live order-book resting liquidity as a horizontal histogram in the right
    /// portion of the price plot. Each level is a thin bar at Y(price), anchored at the plot's right edge
    /// and extending left by (level.Quantity / maxQuantity) × a fraction of the plot width. Bids paint
    /// green, asks red, both at low alpha so the candles underneath stay legible. Levels whose price is
    /// outside the visible Y range are skipped (they'd land off-plot anyway).
    /// </summary>
    private void DrawDepth(ICanvas canvas, RectF plot, Func<double, float> Y)
    {
        if (DepthLevels.Count == 0) return;

        // Normalize against the largest level so the biggest bar spans MaxDepthBarFrac of the plot width
        // and every other bar scales in proportion — a relative "where's the liquidity" read.
        decimal maxQty = 0m;
        for (int i = 0; i < DepthLevels.Count; i++)
            if (DepthLevels[i].Quantity > maxQty) maxQty = DepthLevels[i].Quantity;
        if (maxQty <= 0m) return;

        const float MaxDepthBarFrac = 0.30f;  // biggest bar reaches 30% of the plot width in from the right
        const float BarHalfH = 1.5f;          // half the bar thickness (px)
        float maxW = plot.Width * MaxDepthBarFrac;

        canvas.SaveState();
        for (int i = 0; i < DepthLevels.Count; i++)
        {
            var lvl = DepthLevels[i];
            float y = Y((double)lvl.Price);
            if (y < plot.Top || y > plot.Bottom) continue;  // off-screen level

            float w = (float)((double)(lvl.Quantity / maxQty) * maxW);
            if (w < 1f) w = 1f;
            // Low alpha keeps price legible; bids green, asks red (Binance/TradingView convention).
            canvas.FillColor = (lvl.IsBid ? Bull : Bear).WithAlpha(0.22f);
            canvas.FillRectangle(plot.Right - w, y - BarHalfH, w, BarHalfH * 2f);
        }
        canvas.RestoreState();
    }
}
