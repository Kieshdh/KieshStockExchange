using System.Globalization;
using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Measure-ruler + magnifier box-zoom overlays. Stateless renderer collaborator: geometry and
// inverse transforms arrive in the RenderFrame, colours/fonts in the ChartTheme, and the live
// drag state per call — no reference back to CandleChartDrawable.
internal sealed class MeasureRenderer
{
    private readonly float _rightAxisW;   // gutter width for the measure guide price tags
    public MeasureRenderer(float rightAxisW) => _rightAxisW = rightAxisW;

    // Drag-to-measure overlay: a translucent box between the anchor and the cursor plus a
    // label with Δprice, Δ%, Δtime and #bars. Green tint when the end is at/above the start,
    // red when below — the TradingView measure-tool convention. Inverse transforms keep the
    // readout consistent with the axes under every scale mode.
    public void DrawMeasure(ICanvas canvas, RenderFrame f, ChartTheme t, MeasureState measure)
    {
        if (!measure.Active) return;
        var plot = f.Plot;

        // Clamp the ruler into the plot so the box/label never bleed into the axes gutters.
        float x0 = Math.Clamp(measure.X0, plot.Left, plot.Right);
        float y0 = Math.Clamp(measure.Y0, plot.Top, plot.Bottom);
        float x1 = Math.Clamp(measure.X1, plot.Left, plot.Right);
        float y1 = Math.Clamp(measure.Y1, plot.Top, plot.Bottom);

        if (f.PixelToPrice(y0) is not decimal startPrice || f.PixelToPrice(y1) is not decimal endPrice) return;
        var t0 = f.PixelToTime(x0);
        var t1 = f.PixelToTime(x1);

        bool up = endPrice >= startPrice;
        var tint = up ? t.Bull : t.Bear;

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
        int bars = f.Bucket > TimeSpan.Zero
            ? (int)Math.Round(dt.TotalSeconds / f.Bucket.TotalSeconds)
            : 0;
        var cur = f.Currency;

        // Guide bars: from each measure anchor, a dotted line down to the time axis and right to the price
        // axis, with the price tag in the right gutter + the time tag in the bottom gutter — so both ends'
        // price and time read straight off the axes while measuring (a dual crosshair for the ruler).
        void DrawGuide(float px, float py, decimal price, DateTime time)
        {
            canvas.StrokeColor = tint;
            canvas.StrokeSize = 1f;
            canvas.StrokeDashPattern = new float[] { 2f, 3f };
            canvas.DrawLine(px, py, plot.Right, py);     // → to the price (y) axis
            canvas.DrawLine(px, py, px, plot.Bottom);    // ↓ to the time (x) axis
            canvas.StrokeDashPattern = null;

            var priceTag = new RectF(plot.Right, py - 8f, _rightAxisW, 16f);
            canvas.FillColor = tint;
            canvas.FillRectangle(priceTag);
            canvas.FontColor = Colors.White;
            canvas.FontSize = t.PriceTagFont;
            canvas.DrawString(CurrencyHelper.Format(price, cur),
                new RectF(priceTag.X + 3, priceTag.Y, priceTag.Width - 4, priceTag.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);

            string timeText = time.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
            float ttW = Math.Max(64f, timeText.Length * 6.3f);
            float ttx = Math.Clamp(px - ttW / 2f, plot.Left, Math.Max(plot.Left, plot.Right - ttW));
            var timeTag = new RectF(ttx, plot.Bottom, ttW, 16f);
            canvas.FillColor = tint;
            canvas.FillRectangle(timeTag);
            canvas.DrawString(timeText,
                new RectF(timeTag.X + 3, timeTag.Y, timeTag.Width - 4, timeTag.Height),
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        DrawGuide(x0, y0, startPrice, t0);
        DrawGuide(x1, y1, endPrice, t1);

        string line1 = $"{(dPrice >= 0 ? "+" : "")}{CurrencyHelper.Format(dPrice, cur)}  ({(dPct >= 0 ? "+" : "")}{dPct:0.00}%)";
        string line2 = $"{ChartGeometry.HumanizeSpan(dt)}  ·  {bars} bar{(bars == 1 ? "" : "s")}";

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
        canvas.FontSize = t.PriceTagFont;
        canvas.DrawString(line1, new RectF(panel.X + 5, panel.Y + 2, panel.Width - 10, 14),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.DrawString(line2, new RectF(panel.X + 5, panel.Y + 15, panel.Width - 10, 13),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.RestoreState();
    }

    // Magnifier box-zoom overlay: a dashed selection rectangle with a faint fill between the anchor and
    // the cursor. No readout — on release the viewport zooms to the box (see ChartView). Cleared on release.
    public void DrawZoomBox(ICanvas canvas, RenderFrame f, ChartTheme t, MeasureState zoomBox)
    {
        if (!zoomBox.Active) return;
        var plot = f.Plot;
        float x0 = Math.Clamp(zoomBox.X0, plot.Left, plot.Right);
        float y0 = Math.Clamp(zoomBox.Y0, plot.Top, plot.Bottom);
        float x1 = Math.Clamp(zoomBox.X1, plot.Left, plot.Right);
        float y1 = Math.Clamp(zoomBox.Y1, plot.Top, plot.Bottom);
        var rect = new RectF(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        canvas.SaveState();
        canvas.FillColor = t.CrosshairColor.WithAlpha(0.10f);
        canvas.FillRectangle(rect);
        canvas.StrokeColor = t.CrosshairColor;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 4f, 3f };
        canvas.DrawRectangle(rect);
        canvas.RestoreState();
    }
}
