using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Price series body: the six chart styles (candles/hollow/bars/line/area/Heikin-Ashi) plus the
// live-price dashed line + gutter tag. Stateless renderer collaborator: geometry and transforms
// arrive in the RenderFrame, colours/fonts in the ChartTheme, and the candle series + style +
// live prices per call — no reference back to CandleChartDrawable. Gutter size is the spine's
// layout const, injected once at construction (the AxisRenderer pattern).
internal sealed class CandleRenderer
{
    private readonly float RightAxisW;

    public CandleRenderer(float rightAxisW)
    {
        RightAxisW = rightAxisW;
    }

    public void DrawCandles(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<Candle> candles, ChartStyle style)
    {
        if (candles.Count == 0) return;

        // Line / Area draw the close series as a polyline (optionally gradient-filled).
        if (style == ChartStyle.Line || style == ChartStyle.Area)
        {
            DrawCloseLine(canvas, f, t, candles, filled: style == ChartStyle.Area);
            return;
        }

        // Heikin-Ashi smooths the OHLC off the raw buffer; every other style uses raw OHLC.
        // The raw candle is still what feeds the crosshair OHLCV readout (handled in the VM).
        double[]? haO = null, haH = null, haL = null, haC = null;
        if (style == ChartStyle.HeikinAshi)
            ComputeHeikinAshi(candles, out haO, out haH, out haL, out haC);

        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            float xOpen = f.MapX(c.OpenTime);
            float xClose = f.MapX(c.CloseTime);
            float cx = (xOpen + xClose) * 0.5f;
            // Body takes 70% of the candle slot, leaving gaps between adjacent bars.
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen) * 0.7f);

            double o = haO is null ? (double)c.Open : haO[i];
            double h = haH is null ? (double)c.High : haH[i];
            double l = haL is null ? (double)c.Low : haL[i];
            double cl = haC is null ? (double)c.Close : haC[i];

            float yOpen = f.MapY(o);
            float yClose = f.MapY(cl);
            float yHigh = f.MapY(h);
            float yLow = f.MapY(l);

            bool bull = cl >= o;
            var bodyColor = bull ? t.Bull : t.Bear;

            // OHLC bars: high-low stick with a left open-tick and a right close-tick.
            if (style == ChartStyle.Bars)
            {
                float tick = Math.Max(2f, bodyW * 0.5f);
                canvas.StrokeColor = bodyColor;
                canvas.StrokeSize = 1.4f;
                canvas.DrawLine(cx, yHigh, cx, yLow);
                canvas.DrawLine(cx - tick, yOpen, cx, yOpen);
                canvas.DrawLine(cx, yClose, cx + tick, yClose);
                continue;
            }

            // Wick takes the body colour so the candle reads as one shape.
            canvas.StrokeColor = bodyColor;
            canvas.StrokeSize = 1f;
            canvas.DrawLine(cx, yHigh, cx, yLow);

            // Body — clamp height to 1px minimum so doji candles are still visible.
            float top = Math.Min(yOpen, yClose);
            float bh = Math.Max(1f, Math.Abs(yClose - yOpen));
            var rect = new RectF(cx - bodyW / 2f, top, bodyW, bh);

            // Hollow candles: up bars are outlined (background-filled), down bars stay solid.
            if (style == ChartStyle.HollowCandles && bull)
            {
                canvas.FillColor = t.Bg;
                canvas.FillRectangle(rect);
                canvas.StrokeColor = bodyColor;
                canvas.StrokeSize = 1.2f;
                canvas.DrawRectangle(rect);
            }
            else
            {
                canvas.FillColor = bodyColor;
                canvas.FillRectangle(rect);
            }
        }
    }

    // Line / Area: the close-price polyline. Colour follows the visible window's direction
    // (last close vs first close), matching the TradingView line-chart convention.
    private static void DrawCloseLine(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<Candle> candles, bool filled)
    {
        var plot = f.Plot;
        var lineColor = candles[^1].Close >= candles[0].Close ? t.Bull : t.Bear;
        var stroke = new PathF();
        float firstX = 0f, lastX = 0f;
        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            float x = (f.MapX(c.OpenTime) + f.MapX(c.CloseTime)) * 0.5f;
            float y = f.MapY((double)c.Close);
            if (i == 0) { stroke.MoveTo(x, y); firstX = x; }
            else stroke.LineTo(x, y);
            lastX = x;
        }

        if (filled)
        {
            var fill = new PathF();
            for (int i = 0; i < candles.Count; i++)
            {
                var c = candles[i];
                float x = (f.MapX(c.OpenTime) + f.MapX(c.CloseTime)) * 0.5f;
                float y = f.MapY((double)c.Close);
                if (i == 0) fill.MoveTo(x, y);
                else fill.LineTo(x, y);
            }
            fill.LineTo(lastX, plot.Bottom);
            fill.LineTo(firstX, plot.Bottom);
            fill.Close();
            canvas.FillColor = lineColor.WithAlpha(0.12f);
            canvas.FillPath(fill);
        }

        canvas.StrokeColor = lineColor;
        canvas.StrokeSize = 1.6f;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(stroke);
    }

    // Heikin-Ashi transform of the raw buffer. HA_close = avg(O,H,L,C);
    // HA_open = avg(prev HA open, prev HA close); HA high/low extend to the raw extremes.
    private static void ComputeHeikinAshi(IReadOnlyList<Candle> candles,
        out double[] o, out double[] h, out double[] l, out double[] c)
    {
        int n = candles.Count;
        o = new double[n]; h = new double[n]; l = new double[n]; c = new double[n];
        for (int i = 0; i < n; i++)
        {
            var k = candles[i];
            double ro = (double)k.Open, rh = (double)k.High, rl = (double)k.Low, rc = (double)k.Close;
            double haClose = (ro + rh + rl + rc) / 4.0;
            double haOpen = i == 0 ? (ro + rc) / 2.0 : (o[i - 1] + c[i - 1]) / 2.0;
            o[i] = haOpen;
            c[i] = haClose;
            h[i] = Math.Max(rh, Math.Max(haOpen, haClose));
            l[i] = Math.Min(rl, Math.Min(haOpen, haClose));
        }
    }

    public void DrawCurrentPriceLine(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<Candle> candles, decimal? currentPrice, decimal? sessionOpenPrice)
    {
        if (currentPrice is not decimal price) return;
        if (candles.Count == 0) return;

        // Don't draw when the user has scrolled "now" out of view (either fully into
        // history past the right edge, or way out into synthetic future-space).
        var now = TimeHelper.NowUtc();
        if (now < f.TMin || now > f.TMax) return;

        var plot = f.Plot;
        float y = f.MapY((double)price);
        // Skip drawing if the price falls outside the visible plot.
        if (y < plot.Top || y > plot.Bottom) return;

        // Colour matches the direction of the most recent candle.
        var last = candles[^1];
        bool up = last.Close >= last.Open;
        var color = up ? t.PriceLineUp : t.PriceLineDown;

        // Dashed horizontal line across the plot.
        canvas.SaveState();
        canvas.StrokeColor = color;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 4f, 3f };
        canvas.DrawLine(plot.Left, y, plot.Right, y);
        canvas.StrokeDashPattern = null;
        canvas.RestoreState();

        // Price tag in the right gutter. When a session reference is set, the tag grows to a
        // second line showing the session % change (TradingView axis convention).
        var label = CurrencyHelper.Format(price, f.Currency);
        bool hasPct = sessionOpenPrice is decimal so && so > 0m;
        float tagH = hasPct ? 26f : 16f;
        var tagRect = new RectF(plot.Right + 1, y - tagH / 2f, RightAxisW - 2, tagH);
        canvas.FillColor = color;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = t.PriceTagFont;
        canvas.DrawString(label,
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, hasPct ? 14f : tagRect.Height),
            HorizontalAlignment.Left, hasPct ? VerticalAlignment.Top : VerticalAlignment.Center);
        if (hasPct)
        {
            double pct = ((double)price / (double)sessionOpenPrice!.Value - 1.0) * 100.0;
            canvas.FontSize = t.PriceTagFont - 1f;
            canvas.DrawString($"{(pct >= 0 ? "+" : "")}{pct:0.00}%",
                new RectF(tagRect.X + 3, tagRect.Y + 13f, tagRect.Width - 6, 12f),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }
}
