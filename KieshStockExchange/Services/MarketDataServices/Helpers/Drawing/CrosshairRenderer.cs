using System.Globalization;
using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Crosshair overlay: dashed vertical/horizontal lines with a price tag in the right gutter and a
// time tag on the bottom axis. Stateless renderer collaborator: geometry and inverse transforms
// arrive in the RenderFrame, colours/fonts in the ChartTheme, and the live pointer state per
// call — no reference back to CandleChartDrawable. Gutter sizes are the spine's layout consts,
// injected once at construction (the ChartHitTester pattern).
internal sealed class CrosshairRenderer
{
    private readonly float RightAxisW;
    private readonly float BottomAxisH;

    public CrosshairRenderer(float rightAxisW, float bottomAxisH)
    {
        RightAxisW = rightAxisW;
        BottomAxisH = bottomAxisH;
    }

    public void DrawCrosshair(ICanvas canvas, RenderFrame f, ChartTheme t,
        CrosshairState crosshair, IReadOnlyList<Candle> candles)
    {
        if (!crosshair.Visible) return;
        var plot = f.Plot;
        if (crosshair.X < plot.Left || crosshair.X > plot.Right) return;

        // Pointer can sit in the price area or the volume sub-pane. The vertical
        // line spans both; the horizontal line is drawn within whichever pane the
        // pointer is in, and the price tag only appears for the price pane.
        bool inPrice = crosshair.Y >= plot.Top && crosshair.Y <= plot.Bottom;
        bool inVol = f.VolRect.Height > 0
                     && crosshair.Y >= f.VolRect.Top
                     && crosshair.Y <= f.VolRect.Bottom;
        if (!inPrice && !inVol) return;

        // Snap vertical line to the centre of the hovered candle when there is one.
        float vx = crosshair.X;
        if (crosshair.CandleIndex is int idx && idx >= 0 && idx < candles.Count)
        {
            var c = candles[idx];
            vx = (f.MapX(c.OpenTime) + f.MapX(c.CloseTime)) * 0.5f;
        }

        // Vertical line spans the full chart, including the volume sub-pane.
        float vyTop = plot.Top;
        float vyBottom = f.VolRect.Height > 0 ? f.VolRect.Bottom : plot.Bottom;

        canvas.SaveState();
        canvas.StrokeColor = t.CrosshairColor;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 3f, 3f };
        canvas.DrawLine(vx, vyTop, vx, vyBottom);
        canvas.DrawLine(plot.Left, crosshair.Y, plot.Right, crosshair.Y);
        canvas.StrokeDashPattern = null;

        // Price tag in the right gutter — only when the crosshair Y is in the price pane.
        if (inPrice && f.PixelToPrice(crosshair.Y) is decimal price)
        {
            var tagRect = new RectF(plot.Right + 1, crosshair.Y - 8, RightAxisW - 2, 16);
            canvas.FillColor = t.CrosshairColor;
            canvas.FillRectangle(tagRect);
            canvas.FontColor = Colors.Black;
            canvas.FontSize = t.PriceTagFont;
            canvas.DrawString(CurrencyHelper.Format(price, f.Currency),
                new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        // Time tag on the bottom axis at the crosshair X.
        var time = f.PixelToTime(vx);
        var timeText = time.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture);
        var timeRect = new RectF(vx - 50, vyBottom + 2, 100, BottomAxisH - 2);
        canvas.FillColor = t.CrosshairColor;
        canvas.FillRectangle(timeRect);
        canvas.FontColor = Colors.Black;
        canvas.FontSize = t.AxisFont;
        canvas.DrawString(timeText, timeRect, HorizontalAlignment.Center, VerticalAlignment.Center);

        canvas.RestoreState();
    }
}
