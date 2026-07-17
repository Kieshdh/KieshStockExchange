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
    #region Crosshair drawing
    private void DrawCrosshair(ICanvas canvas, RectF plot, CurrencyType cur, Func<DateTime, float> X)
    {
        if (!Crosshair.Visible) return;
        if (Crosshair.X < plot.Left || Crosshair.X > plot.Right) return;

        // Pointer can sit in the price area or the volume sub-pane. The vertical
        // line spans both; the horizontal line is drawn within whichever pane the
        // pointer is in, and the price tag only appears for the price pane.
        bool inPrice = Crosshair.Y >= plot.Top && Crosshair.Y <= plot.Bottom;
        bool inVol = _lastVolRect.Height > 0
                     && Crosshair.Y >= _lastVolRect.Top
                     && Crosshair.Y <= _lastVolRect.Bottom;
        if (!inPrice && !inVol) return;

        // Snap vertical line to the centre of the hovered candle when there is one.
        float vx = Crosshair.X;
        if (Crosshair.CandleIndex is int idx && idx >= 0 && idx < Candles.Count)
        {
            var c = Candles[idx];
            vx = (X(c.OpenTime) + X(c.CloseTime)) * 0.5f;
        }

        // Vertical line spans the full chart, including the volume sub-pane.
        float vyTop = plot.Top;
        float vyBottom = _lastVolRect.Height > 0 ? _lastVolRect.Bottom : plot.Bottom;

        canvas.SaveState();
        canvas.StrokeColor = CrosshairColor;
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = new float[] { 3f, 3f };
        canvas.DrawLine(vx, vyTop, vx, vyBottom);
        canvas.DrawLine(plot.Left, Crosshair.Y, plot.Right, Crosshair.Y);
        canvas.StrokeDashPattern = null;

        // Price tag in the right gutter — only when the crosshair Y is in the price pane.
        if (inPrice && PixelToPrice(Crosshair.Y) is decimal price)
        {
            var tagRect = new RectF(plot.Right + 1, Crosshair.Y - 8, RightAxisW - 2, 16);
            canvas.FillColor = CrosshairColor;
            canvas.FillRectangle(tagRect);
            canvas.FontColor = Colors.Black;
            canvas.FontSize = PriceTagFont;
            canvas.DrawString(CurrencyHelper.Format(price, cur),
                new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        // Time tag on the bottom axis at the crosshair X.
        var t = PixelToTime(vx);
        var timeText = t.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.InvariantCulture);
        var timeRect = new RectF(vx - 50, vyBottom + 2, 100, BottomAxisH - 2);
        canvas.FillColor = CrosshairColor;
        canvas.FillRectangle(timeRect);
        canvas.FontColor = Colors.Black;
        canvas.FontSize = AxisFont;
        canvas.DrawString(timeText, timeRect, HorizontalAlignment.Center, VerticalAlignment.Center);

        canvas.RestoreState();
    }
    #endregion
}
