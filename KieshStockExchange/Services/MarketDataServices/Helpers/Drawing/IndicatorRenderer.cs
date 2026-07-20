using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Indicator passes: MA polylines over the price plot, volume bars (overlay or sub-pane),
// the Fear/Greed mood strip, and the order-book depth histogram. Stateless renderer
// collaborator: geometry and transforms arrive in the RenderFrame (f.VolRect / f.MoodRect /
// f.Bucket carry the pane rects + half-bucket MA shift), colours/fonts in the ChartTheme,
// and the series per call — no reference back to CandleChartDrawable. Gutter size is the
// spine's layout const, injected once at construction (the CandleRenderer pattern).
internal sealed class IndicatorRenderer
{
    private readonly float RightAxisW;

    public IndicatorRenderer(float rightAxisW)
    {
        RightAxisW = rightAxisW;
    }

    public void DrawMovingAverages(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<MovingAverageSeries> maSeries, bool viewportValid)
    {
        if (maSeries.Count == 0) return;

        // MaPoint.AtTime is OpenTime; shift to candle centre so the line tracks the middle
        // of each bucket rather than the left edge. Guard on the source viewport's validity
        // (Bucket > 0 AND ViewEnd > ViewStart), not just Bucket > 0, to match the pre-split
        // behaviour exactly — a degenerate range with a positive bucket must not shift.
        var halfBucket = viewportValid
            ? TimeSpan.FromTicks(f.Bucket.Ticks / 2)
            : TimeSpan.Zero;

        canvas.SaveState();
        canvas.StrokeSize = 1.5f;
        foreach (var series in maSeries)
        {
            var pts = series.Points;
            if (pts == null || pts.Count == 0) continue;

            canvas.StrokeColor = series.Color;
            var path = new PathF();
            bool started = false;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var time = p.AtTime + halfBucket;
                if (time < f.TMin) continue;
                if (time > f.TMax) break;
                if (p.Value < f.YMin || p.Value > f.YMax) { started = false; continue; }

                float px = f.MapX(time);
                float py = f.MapY(p.Value);
                if (!started) { path.MoveTo(px, py); started = true; }
                else path.LineTo(px, py);
            }
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    public void DrawBollinger(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<BollingerPoint> series, Color color, bool viewportValid)
    {
        if (series.Count == 0) return;

        // BollingerPoint.Time is OpenTime; shift to candle centre so the bands track the middle of
        // each bucket rather than its left edge — identical clip/shift to the MA pass above.
        var halfBucket = viewportValid
            ? TimeSpan.FromTicks(f.Bucket.Ticks / 2)
            : TimeSpan.Zero;

        // Three parallel polylines share the same X sweep; each carries its own started flag so a
        // value clipped out of the Y range breaks only its own band, not the others.
        var mid = new PathF(); bool midStarted = false;
        var upper = new PathF(); bool upperStarted = false;
        var lower = new PathF(); bool lowerStarted = false;
        for (int i = 0; i < series.Count; i++)
        {
            var p = series[i];
            var time = p.Time + halfBucket;
            if (time < f.TMin) continue;
            if (time > f.TMax) break;
            float px = f.MapX(time);
            AppendBandPoint(mid, ref midStarted, f, px, p.Middle);
            AppendBandPoint(upper, ref upperStarted, f, px, p.Upper);
            AppendBandPoint(lower, ref lowerStarted, f, px, p.Lower);
        }

        canvas.SaveState();
        canvas.StrokeSize = 1.5f;
        // Envelope a touch lighter so the SMA middle reads as the primary line.
        canvas.StrokeColor = color.WithAlpha(0.7f);
        canvas.DrawPath(upper);
        canvas.DrawPath(lower);
        canvas.StrokeColor = color;
        canvas.DrawPath(mid);
        canvas.RestoreState();
    }

    // One band vertex: Y-range guard (break the line on a clipped value) + started-flag bookkeeping,
    // matching the MA polyline's per-point handling.
    private static void AppendBandPoint(PathF path, ref bool started, RenderFrame f, float px, double value)
    {
        if (value < f.YMin || value > f.YMax) { started = false; return; }
        float py = f.MapY(value);
        if (!started) { path.MoveTo(px, py); started = true; }
        else path.LineTo(px, py);
    }

    public void DrawVwap(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<VwapPoint> series, Color color, bool viewportValid)
    {
        if (series.Count == 0) return;

        // VwapPoint.Time is OpenTime; same half-bucket centre-shift + viewport clip as the MA pass.
        var halfBucket = viewportValid
            ? TimeSpan.FromTicks(f.Bucket.Ticks / 2)
            : TimeSpan.Zero;

        canvas.SaveState();
        canvas.StrokeSize = 1.5f;
        canvas.StrokeColor = color;
        var path = new PathF();
        bool started = false;
        for (int i = 0; i < series.Count; i++)
        {
            var p = series[i];
            var time = p.Time + halfBucket;
            if (time < f.TMin) continue;
            if (time > f.TMax) break;
            if (p.Value < f.YMin || p.Value > f.YMax) { started = false; continue; }

            float px = f.MapX(time);
            float py = f.MapY(p.Value);
            if (!started) { path.MoveTo(px, py); started = true; }
            else path.LineTo(px, py);
        }
        canvas.DrawPath(path);
        canvas.RestoreState();
    }

    public void DrawVolume(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<Candle> candles, bool overlayVolume)
    {
        var volRect = f.VolRect;

        // Sub-pane mode draws a border so the panel reads as distinct. Overlay
        // mode shares the price plot's rectangle so a second border would just
        // cut a horizontal line across the chart.
        if (!overlayVolume)
        {
            canvas.StrokeColor = t.Grid;
            canvas.StrokeSize = 1f;
            canvas.DrawRectangle(volRect);
        }

        if (candles.Count == 0) return;

        long maxVol = 0L;
        for (int i = 0; i < candles.Count; i++)
            if (candles[i].Volume > maxVol) maxVol = candles[i].Volume;
        if (maxVol <= 0) return;

        // Overlay mode lowers alpha further so candles drawn on top stay legible.
        float overlayAlpha = overlayVolume ? 0.35f : 0.6f;
        var bullTint = t.VolumeBullTint.Alpha > 0 ? t.VolumeBullTint : t.Bull.WithAlpha(overlayAlpha);
        var bearTint = t.VolumeBearTint.Alpha > 0 ? t.VolumeBearTint : t.Bear.WithAlpha(overlayAlpha);

        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            float xOpen = f.MapX(c.OpenTime);
            float xClose = f.MapX(c.CloseTime);
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
    /// the axis; the line is plotted against the shared MapX time transform so it lines up with the candles.
    /// A current-value pill (score + label) sits top-left of the strip.
    /// </summary>
    public void DrawMood(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<(DateTime Time, double Value)> moodSeries)
    {
        var r = f.MoodRect;

        // Sub-pane border, matching the volume sub-pane.
        canvas.StrokeColor = t.Grid;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(r);

        float MoodY(double v) => r.Bottom - (float)(Math.Clamp(v, 0, 100) / 100.0 * r.Height);

        // Fear (<30) / greed (>70) zone washes so the strip reads at a glance.
        float y30 = MoodY(30), y70 = MoodY(70);
        canvas.FillColor = t.Bear.WithAlpha(0.10f);
        canvas.FillRectangle(r.X, y30, r.Width, r.Bottom - y30);
        canvas.FillColor = t.Bull.WithAlpha(0.10f);
        canvas.FillRectangle(r.X, r.Y, r.Width, y70 - r.Y);

        // 0 / 50 / 100 gridlines + right-gutter labels.
        canvas.FontColor = t.Axis;
        canvas.FontSize = t.AxisFont;
        foreach (var lvl in new[] { 0.0, 50.0, 100.0 })
        {
            float y = MoodY(lvl);
            canvas.StrokeColor = t.Grid.WithAlpha(0.5f);
            canvas.StrokeSize = 1f;
            canvas.DrawLine(r.Left, y, r.Right, y);
            canvas.DrawString($"{lvl:0}",
                new RectF(r.Right + 4, y - 7, RightAxisW - 8, 14),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        // Accumulated series polyline. Clipped to the visible time window; values map on the fixed scale.
        if (moodSeries.Count > 0)
        {
            var path = new PathF();
            bool started = false;
            for (int i = 0; i < moodSeries.Count; i++)
            {
                var (time, v) = moodSeries[i];
                if (time < f.TMin || time > f.TMax) { started = false; continue; }
                float px = f.MapX(time), py = MoodY(v);
                if (!started) { path.MoveTo(px, py); started = true; }
                else path.LineTo(px, py);
            }
            canvas.StrokeColor = t.MoodLineColor;
            canvas.StrokeSize = 1.6f;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);

            // Current-value pill top-left: latest score + fear/greed word, tinted by zone.
            double latest = moodSeries[^1].Value;
            var (word, tint) = latest >= 70 ? ("Greed", t.Bull)
                             : latest <= 30 ? ("Fear", t.Bear)
                             : ("Neutral", t.MoodLineColor);
            string label = $"Mood {latest:0}  {word}";
            var pill = new RectF(r.Left + 4, r.Top + 3, Math.Max(96f, label.Length * 6.5f), 15f);
            canvas.FillColor = tint.WithAlpha(0.85f);
            canvas.FillRectangle(pill);
            canvas.FontColor = Colors.White;
            canvas.FontSize = t.AxisFont;
            canvas.DrawString(label, new RectF(pill.X + 4, pill.Y, pill.Width - 6, pill.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }

    /// <summary>
    /// §depth-overlay: draw the live order-book resting liquidity as a horizontal histogram in the right
    /// portion of the price plot. Each level is a thin bar at MapY(price), anchored at the plot's right edge
    /// and extending left by (level.Quantity / maxQuantity) × a fraction of the plot width. Bids paint
    /// green, asks red, both at low alpha so the candles underneath stay legible. Levels whose price is
    /// outside the visible Y range are skipped (they'd land off-plot anyway).
    /// </summary>
    public void DrawDepth(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<DepthLevel> depthLevels)
    {
        if (depthLevels.Count == 0) return;

        var plot = f.Plot;

        // Normalize against the largest level so the biggest bar spans MaxDepthBarFrac of the plot width
        // and every other bar scales in proportion — a relative "where's the liquidity" read.
        decimal maxQty = 0m;
        for (int i = 0; i < depthLevels.Count; i++)
            if (depthLevels[i].Quantity > maxQty) maxQty = depthLevels[i].Quantity;
        if (maxQty <= 0m) return;

        const float MaxDepthBarFrac = 0.30f;  // biggest bar reaches 30% of the plot width in from the right
        const float BarHalfH = 1.5f;          // half the bar thickness (px)
        float maxW = plot.Width * MaxDepthBarFrac;

        canvas.SaveState();
        for (int i = 0; i < depthLevels.Count; i++)
        {
            var lvl = depthLevels[i];
            float y = f.MapY((double)lvl.Price);
            if (y < plot.Top || y > plot.Bottom) continue;  // off-screen level

            float w = (float)((double)(lvl.Quantity / maxQty) * maxW);
            if (w < 1f) w = 1f;
            // Low alpha keeps price legible; bids green, asks red (Binance/TradingView convention).
            canvas.FillColor = (lvl.IsBid ? t.Bull : t.Bear).WithAlpha(0.22f);
            canvas.FillRectangle(plot.Right - w, y - BarHalfH, w, BarHalfH * 2f);
        }
        canvas.RestoreState();
    }
}
