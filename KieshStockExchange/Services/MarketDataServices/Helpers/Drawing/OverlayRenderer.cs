using KieshStockExchange.Helpers;
using KieshStockExchange.Models;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// Trading overlays: open-order lines, the position line, fill triangles and fired-trigger
// arrows. Stateless renderer collaborator: geometry and transforms arrive in the RenderFrame
// (f.Plot / f.MapX / f.MapY / f.Currency), colours/fonts in the ChartTheme, and the order/
// position/marker inputs per call — no reference back to CandleChartDrawable. Gutter size is
// the spine's layout const, injected once at construction (the CandleRenderer pattern).
internal sealed class OverlayRenderer
{
    private readonly float RightAxisW;

    public OverlayRenderer(float rightAxisW)
    {
        RightAxisW = rightAxisW;
    }

    /// <summary>
    /// Draw a dashed horizontal line at each open-order price.  TradingView /
    /// Binance convention:
    ///   - Right-gutter tag shows the price (matching the live-price tag style),
    ///     filled with the side colour and white-on-coloured text.
    ///   - On-chart inline label "B 10" / "S 10" sits just inside the right edge
    ///     of the plot so the user sees side + quantity at a glance without the
    ///     numbers clashing with the gridline price tags.
    /// </summary>
    public void DrawOpenOrderLines(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<OpenOrderLine> openOrderLines, int? draggingOrderId, decimal? draggingOrderPrice)
    {
        if (openOrderLines.Count == 0) return;
        var plot = f.Plot;
        canvas.SaveState();
        for (int i = 0; i < openOrderLines.Count; i++)
        {
            var line = openOrderLines[i];
            bool dragging = draggingOrderId == line.OrderId;
            decimal price = dragging && draggingOrderPrice is decimal dp ? dp : line.Price;
            float y = f.MapY((double)price);
            if (y < plot.Top || y > plot.Bottom) continue;

            // §3.6 P3: a stop trigger line is amber with a tighter dash so it stands apart
            // from the green/red resting-limit lines; limits keep the {4,4} dash + side colour.
            // §F12: a dormant bracket child draws in the same colour but at 45 % alpha to convey
            // "not live yet" — the parent hasn't filled, so the leg hasn't armed/rested either.
            var baseColor = line.IsStop ? t.OpenOrderStopColor : (line.IsBuy ? t.OpenOrderBuyColor : t.OpenOrderSellColor);
            var color = line.IsDormant ? baseColor.WithAlpha(0.45f) : baseColor;
            canvas.StrokeColor = color;
            canvas.StrokeSize = dragging ? 2f : 1f;
            canvas.StrokeDashPattern = line.IsStop ? new float[] { 2f, 3f } : new float[] { 4f, 4f };
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            canvas.StrokeDashPattern = null;

            // Right-gutter tag: price, like a TradingView order line.
            var tagRect = new RectF(plot.Right + 1, y - 8, RightAxisW - 2, 16);
            canvas.FillColor = color;
            canvas.FillRectangle(tagRect);
            canvas.FontColor = Colors.White;
            canvas.FontSize = t.PriceTagFont;
            canvas.DrawString(CurrencyHelper.Format(price, f.Currency),
                new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, tagRect.Height),
                HorizontalAlignment.Left, VerticalAlignment.Center);

            // Inline label hugged to the right edge of the plot, on top of the dashed
            // line. Small pill so it stays readable against candles. §3.6 P3: a stop reads
            // STOP/STOP-LIM + qty; a resting limit reads B/S + qty. §F12: dormant legs label
            // as TP/SL so the user knows they're bracket children, not standalone resting orders.
            var labelText = line.IsStop
                ? $"{(line.IsStopLimit ? "STOP-LIM" : (line.IsDormant ? "SL" : "STOP"))} {line.Quantity}"
                : (line.IsDormant ? $"TP {line.Quantity}" : $"{(line.IsBuy ? "B" : "S")} {line.Quantity}");
            float labelW = Math.Max(28f, labelText.Length * 7f);
            var labelRect = new RectF(plot.Right - labelW - 4f, y - 8, labelW, 16);
            canvas.FillColor = color;
            canvas.FillRectangle(labelRect);
            canvas.FontColor = Colors.White;
            canvas.DrawString(labelText, labelRect,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
        canvas.RestoreState();
    }

    /// <summary>
    /// Draw the user's open position TradingView-style: a solid horizontal line at the average
    /// entry price in the teal position colour, an inline LONG/SHORT + size pill hugged to the
    /// right edge, and a right-gutter tag showing the live unrealized P&L (currency on top, % below)
    /// tinted green when in profit / red when in loss. Skipped when flat or off-screen.
    /// </summary>
    public void DrawPositionLine(ICanvas canvas, RenderFrame f, ChartTheme t, PositionLine? position)
    {
        if (position is not PositionLine pos || pos.Quantity == 0m) return;
        var plot = f.Plot;
        float y = f.MapY((double)pos.AvgPrice);
        if (y < plot.Top || y > plot.Bottom) return;

        bool profit = pos.UnrealizedPnl >= 0m;
        var pnlColor = profit ? t.Bull : t.Bear;

        canvas.SaveState();
        // Solid line — contrasts with the dashed order / live-price lines.
        canvas.StrokeColor = t.PositionLineColor;
        canvas.StrokeSize = 1.5f;
        canvas.DrawLine(plot.Left, y, plot.Right, y);

        // Inline size pill in the position colour, hugged to the right edge of the plot.
        bool isLong = pos.Quantity > 0m;
        string sizeText = $"{(isLong ? "LONG" : "SHORT")} {Math.Abs(pos.Quantity):0.####}";
        float sizeW = Math.Max(52f, sizeText.Length * 7f);
        var sizeRect = new RectF(plot.Right - sizeW - 4f, y - 8, sizeW, 16);
        canvas.FillColor = t.PositionLineColor;
        canvas.FillRectangle(sizeRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = t.PriceTagFont;
        canvas.DrawString(sizeText, sizeRect, HorizontalAlignment.Center, VerticalAlignment.Center);

        // Right-gutter P&L tag: currency on top, % below, tinted by profit/loss.
        var tagRect = new RectF(plot.Right + 1, y - 13, RightAxisW - 2, 26);
        canvas.FillColor = pnlColor;
        canvas.FillRectangle(tagRect);
        canvas.FontColor = Colors.White;
        canvas.FontSize = t.PriceTagFont;
        canvas.DrawString($"{(pos.UnrealizedPnl >= 0m ? "+" : "")}{CurrencyHelper.Format(pos.UnrealizedPnl, f.Currency)}",
            new RectF(tagRect.X + 3, tagRect.Y, tagRect.Width - 6, 14f),
            HorizontalAlignment.Left, VerticalAlignment.Top);
        canvas.FontSize = t.PriceTagFont - 1f;
        canvas.DrawString($"{(pos.UnrealizedPct >= 0 ? "+" : "")}{pos.UnrealizedPct:0.00}%",
            new RectF(tagRect.X + 3, tagRect.Y + 13f, tagRect.Width - 6, 12f),
            HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.RestoreState();
    }

    /// <summary>
    /// Draw the user's executed fills as small, tall triangles at (fill time, fill price).
    /// A buy is an up-pointing triangle sitting just BELOW the price (apex pointing up at it);
    /// a sell is a down-pointing triangle just ABOVE the price (apex pointing down at it). The
    /// base is short and the sides long so the markers read as crisp arrows, not blobs.
    /// </summary>
    public void DrawFillMarkers(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<FillMarker> fillMarkers, IReadOnlyList<Candle> candles)
    {
        if (fillMarkers.Count == 0) return;
        var plot = f.Plot;
        const float baseHalf = 4f;  // half the (short) base → 8px wide
        const float height = 16f;   // long sides → tall arrow
        const float gap = 5f;       // offset between the apex and the fill price
        var outline = t.Outline();
        canvas.SaveState();
        for (int i = 0; i < fillMarkers.Count; i++)
        {
            var m = fillMarkers[i];
            // Snap to the center of the candle that contains the fill time so the arrow lines up
            // with its bar (esp. on higher timeframes) instead of landing between two candles.
            float x = SnapToCandleCenterX(f, candles, m.AtTime);
            if (x < plot.Left || x > plot.Right) continue;
            float yPrice = f.MapY((double)m.Price);
            if (yPrice < plot.Top || yPrice > plot.Bottom) continue;

            var path = new PathF();
            if (m.IsBuy)
            {
                // Up triangle below the price: apex (top) points up toward the fill.
                float apexY = yPrice + gap;
                float baseY = apexY + height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
                canvas.FillColor = t.FillBuyColor;
            }
            else
            {
                // Down triangle above the price: apex (bottom) points down toward the fill.
                float apexY = yPrice - gap;
                float baseY = apexY - height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
                canvas.FillColor = t.FillSellColor;
            }
            canvas.FillPath(path);
            // Theme-aware outline: keeps the arrow legible against both candles and the background.
            canvas.StrokeColor = outline;
            canvas.StrokeSize = 1f;
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    /// <summary>
    /// §F2: draw each fired trigger as a hollow blue arrow at (activation time, trigger price).
    /// Larger than a fill triangle and outlined-not-filled, so a coincident fill + trigger reads as
    /// two distinct things: "filled here" (solid green/red) vs "trigger crossed here" (blue arrow).
    /// Up for a buy trigger, down for a sell.
    /// </summary>
    public void DrawTriggerMarkers(ICanvas canvas, RenderFrame f, ChartTheme t,
        IReadOnlyList<TriggerMarker> triggerMarkers, IReadOnlyList<Candle> candles)
    {
        if (triggerMarkers.Count == 0) return;
        var plot = f.Plot;
        const float baseHalf = 6f;  // wider than the fill triangle (4f)
        const float height = 18f;   // taller too
        const float gap = 6f;
        canvas.SaveState();
        for (int i = 0; i < triggerMarkers.Count; i++)
        {
            var m = triggerMarkers[i];
            float x = SnapToCandleCenterX(f, candles, m.AtTime);
            if (x < plot.Left || x > plot.Right) continue;
            float yPrice = f.MapY((double)m.Price);
            if (yPrice < plot.Top || yPrice > plot.Bottom) continue;

            var path = new PathF();
            if (m.IsBuy)
            {
                // Up arrow below the trigger price: apex points up toward the level.
                float apexY = yPrice + gap;
                float baseY = apexY + height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
            }
            else
            {
                // Down arrow above the trigger price: apex points down toward the level.
                float apexY = yPrice - gap;
                float baseY = apexY - height;
                path.MoveTo(x, apexY);
                path.LineTo(x - baseHalf, baseY);
                path.LineTo(x + baseHalf, baseY);
                path.Close();
            }
            // Hollow: low-alpha blue wash + a bold blue outline, vs the fill triangle's solid fill.
            canvas.FillColor = t.TriggerColor.WithAlpha(0.22f);
            canvas.FillPath(path);
            canvas.StrokeColor = t.TriggerColor;
            canvas.StrokeSize = 2f;
            canvas.DrawPath(path);
        }
        canvas.RestoreState();
    }

    // Center x of the candle bucket that contains time; falls back to the raw time x when no candle
    // covers it (a fill in a gap / outside the loaded slice).
    private static float SnapToCandleCenterX(RenderFrame f, IReadOnlyList<Candle> candles, DateTime time)
    {
        int lo = 0, hi = candles.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var c = candles[mid];
            if (time < c.OpenTime) hi = mid - 1;
            else if (time >= c.CloseTime) lo = mid + 1;
            else return (f.MapX(c.OpenTime) + f.MapX(c.CloseTime)) * 0.5f;
        }
        return f.MapX(time);
    }
}
