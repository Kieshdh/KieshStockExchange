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
    #region Measure ruler drawing
    // Drag-to-measure overlay: a translucent box between the anchor and the cursor plus a
    // label with Δprice, Δ%, Δtime and #bars. Green tint when the end is at/above the start,
    // red when below — the TradingView measure-tool convention. Inverse transforms keep the
    // readout consistent with the axes under every scale mode.
    private void DrawMeasure(ICanvas canvas, RectF plot)
    {
        if (!Measure.Active) return;

        // Clamp the ruler into the plot so the box/label never bleed into the axes gutters.
        float x0 = Math.Clamp(Measure.X0, plot.Left, plot.Right);
        float y0 = Math.Clamp(Measure.Y0, plot.Top, plot.Bottom);
        float x1 = Math.Clamp(Measure.X1, plot.Left, plot.Right);
        float y1 = Math.Clamp(Measure.Y1, plot.Top, plot.Bottom);

        if (PixelToPrice(y0) is not decimal startPrice || PixelToPrice(y1) is not decimal endPrice) return;
        var t0 = PixelToTime(x0);
        var t1 = PixelToTime(x1);

        bool up = endPrice >= startPrice;
        var tint = up ? Bull : Bear;

        // Translucent box + border.
        canvas.SaveState();
        var rect = new RectF(Math.Min(x0, x1), Math.Min(y0, y1),
                             Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        canvas.FillColor = tint.WithAlpha(0.15f);
        canvas.FillRectangle(rect);
        canvas.StrokeColor = tint;
        canvas.StrokeSize = 1f;
        canvas.DrawRectangle(rect);

        // Readout deltas.
        decimal dPrice = endPrice - startPrice;
        double dPct = startPrice != 0m ? ((double)(endPrice / startPrice) - 1.0) * 100.0 : 0.0;
        var dt = t1 - t0;
        if (dt < TimeSpan.Zero) dt = dt.Negate();
        int bars = Viewport.Bucket > TimeSpan.Zero
            ? (int)Math.Round(dt.TotalSeconds / Viewport.Bucket.TotalSeconds)
            : 0;
        var cur = Candles.Count > 0 ? Candles[0].CurrencyType : CurrencyType.USD;

        string line1 = $"{(dPrice >= 0 ? "+" : "")}{CurrencyHelper.Format(dPrice, cur)}  ({(dPct >= 0 ? "+" : "")}{dPct:0.00}%)";
        string line2 = $"{HumanizeSpan(dt)}  ·  {bars} bar{(bars == 1 ? "" : "s")}";

        // Label panel anchored at the cursor end, flipped to stay inside the plot.
        float panelW = Math.Max(120f, Math.Max(line1.Length, line2.Length) * 6.5f);
        const float panelH = 30f;
        float lx = x1 + 8f;
        if (lx + panelW > plot.Right) lx = x1 - panelW - 8f;
        lx = Math.Clamp(lx, plot.Left, Math.Max(plot.Left, plot.Right - panelW));
        float ly = y1 - panelH - 6f;
        if (ly < plot.Top) ly = y1 + 6f;
        ly = Math.Clamp(ly, plot.Top, Math.Max(plot.Top, plot.Bottom - panelH));
        var panel = new RectF(lx, ly, panelW, panelH);
        canvas.FillColor = tint;
        canvas.FillRectangle(panel);
        canvas.FontColor = Colors.White;
        canvas.FontSize = PriceTagFont;
        canvas.DrawString(line1, new RectF(panel.X + 5, panel.Y + 2, panel.Width - 10, 14),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.DrawString(line2, new RectF(panel.X + 5, panel.Y + 15, panel.Width - 10, 13),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.RestoreState();
    }

    // Human-friendly span for the measure label: coarsest two units that carry signal.
    private static string HumanizeSpan(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }

    // Magnifier box-zoom overlay: a dashed selection rectangle with a faint fill between the anchor and
    // the cursor. No readout — on release the viewport zooms to the box (see ChartView). Cleared on release.
    private void DrawZoomBox(ICanvas canvas, RectF plot)
    {
        if (!ZoomBox.Active) return;
        float x0 = Math.Clamp(ZoomBox.X0, plot.Left, plot.Right);
        float y0 = Math.Clamp(ZoomBox.Y0, plot.Top, plot.Bottom);
        float x1 = Math.Clamp(ZoomBox.X1, plot.Left, plot.Right);
        float y1 = Math.Clamp(ZoomBox.Y1, plot.Top, plot.Bottom);
        var rect = new RectF(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        canvas.SaveState();
        canvas.FillColor = CrosshairColor.WithAlpha(0.10f);
        canvas.FillRectangle(rect);
        canvas.StrokeColor = CrosshairColor;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 4f, 3f };
        canvas.DrawRectangle(rect);
        canvas.RestoreState();
    }
    #endregion
}
