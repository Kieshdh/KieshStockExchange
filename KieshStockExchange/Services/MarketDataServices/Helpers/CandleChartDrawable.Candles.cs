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
    #region Candle Drawing
    private void DrawCandles(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y)
    {
        if (Candles.Count == 0) return;

        // Line / Area draw the close series as a polyline (optionally gradient-filled).
        if (Style == ChartStyle.Line || Style == ChartStyle.Area)
        {
            DrawCloseLine(canvas, plot, X, Y, filled: Style == ChartStyle.Area);
            return;
        }

        // Heikin-Ashi smooths the OHLC off the raw buffer; every other style uses raw OHLC.
        // The raw candle is still what feeds the crosshair OHLCV readout (handled in the VM).
        double[]? haO = null, haH = null, haL = null, haC = null;
        if (Style == ChartStyle.HeikinAshi)
            ComputeHeikinAshi(out haO, out haH, out haL, out haC);

        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float xOpen = X(c.OpenTime);
            float xClose = X(c.CloseTime);
            float cx = (xOpen + xClose) * 0.5f;
            // Body takes 70% of the candle slot, leaving gaps between adjacent bars.
            float bodyW = Math.Max(1f, Math.Abs(xClose - xOpen) * 0.7f);

            double o = haO is null ? (double)c.Open : haO[i];
            double h = haH is null ? (double)c.High : haH[i];
            double l = haL is null ? (double)c.Low : haL[i];
            double cl = haC is null ? (double)c.Close : haC[i];

            float yOpen = Y(o);
            float yClose = Y(cl);
            float yHigh = Y(h);
            float yLow = Y(l);

            bool bull = cl >= o;
            var bodyColor = bull ? Bull : Bear;

            // OHLC bars: high-low stick with a left open-tick and a right close-tick.
            if (Style == ChartStyle.Bars)
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
            if (Style == ChartStyle.HollowCandles && bull)
            {
                canvas.FillColor = Bg;
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
    private void DrawCloseLine(ICanvas canvas, RectF plot, Func<DateTime, float> X, Func<double, float> Y, bool filled)
    {
        var lineColor = Candles[^1].Close >= Candles[0].Close ? Bull : Bear;
        var stroke = new PathF();
        float firstX = 0f, lastX = 0f;
        for (int i = 0; i < Candles.Count; i++)
        {
            var c = Candles[i];
            float x = (X(c.OpenTime) + X(c.CloseTime)) * 0.5f;
            float y = Y((double)c.Close);
            if (i == 0) { stroke.MoveTo(x, y); firstX = x; }
            else stroke.LineTo(x, y);
            lastX = x;
        }

        if (filled)
        {
            var fill = new PathF();
            for (int i = 0; i < Candles.Count; i++)
            {
                var c = Candles[i];
                float x = (X(c.OpenTime) + X(c.CloseTime)) * 0.5f;
                float y = Y((double)c.Close);
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
    private void ComputeHeikinAshi(out double[] o, out double[] h, out double[] l, out double[] c)
    {
        int n = Candles.Count;
        o = new double[n]; h = new double[n]; l = new double[n]; c = new double[n];
        for (int i = 0; i < n; i++)
        {
            var k = Candles[i];
            double ro = (double)k.Open, rh = (double)k.High, rl = (double)k.Low, rc = (double)k.Close;
            double haClose = (ro + rh + rl + rc) / 4.0;
            double haOpen = i == 0 ? (ro + rc) / 2.0 : (o[i - 1] + c[i - 1]) / 2.0;
            o[i] = haOpen;
            c[i] = haClose;
            h[i] = Math.Max(rh, Math.Max(haOpen, haClose));
            l[i] = Math.Min(rl, Math.Min(haOpen, haClose));
        }
    }

    private void DrawCurrentPriceLine(ICanvas canvas, RectF plot, Func<double, float> Y, CurrencyType cur,
        DateTime tMin, DateTime tMax)
    {
        if (CurrentPrice is not decimal price) return;
        if (Candles.Count == 0) return;

        // Don't draw when the user has scrolled "now" out of view (either fully into
        // history past the right edge, or way out into synthetic future-space).
        var now = TimeHelper.NowUtc();
        if (now < tMin || now > tMax) return;

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

        // Price tag in the right gutter. When a session reference is set, the tag grows to a
        // second line showing the session % change (TradingView axis convention).
        var label = CurrencyHelper.Format(price, cur);
        bool hasPct = SessionOpenPrice is decimal so && so > 0m;
        float tagH = hasPct ? 26f : 16f;
        var tagRect = new RectF(plot.Right + 1, y - tagH / 2f, RightAxisW - 2, tagH);
        canvas.FillColor = color;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(label,
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, hasPct ? 14f : tagRect.Height),
            HorizontalAlignment.Left, hasPct ? VerticalAlignment.Top : VerticalAlignment.Center);
        if (hasPct)
        {
            double pct = ((double)price / (double)SessionOpenPrice!.Value - 1.0) * 100.0;
            canvas.FontSize = PriceTagFont - 1f;
            canvas.DrawString($"{(pct >= 0 ? "+" : "")}{pct:0.00}%",
                new RectF(tagRect.X + 3, tagRect.Y + 13f, tagRect.Width - 6, 12f),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }
    }
    #endregion
}
