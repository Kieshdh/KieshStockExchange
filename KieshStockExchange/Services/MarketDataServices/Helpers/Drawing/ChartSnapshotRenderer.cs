using System.IO;
using Microsoft.Maui.Graphics.Skia;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// UP-CORE: renders a CandleChartDrawable offscreen to PNG bytes (deterministic, headless). LP1 wires
// the button/file/open. Uses Skia's offscreen canvas — CandleChartDrawable lives in the enclosing
// ...MarketDataServices namespace, so it resolves without an extra using.
//
// NOTE: this depends on Microsoft.Maui.Graphics.Skia (added to the client csproj) and on the full
// drawable, so it is intentionally NOT part of the headless unit-test link set.
public static class ChartSnapshotRenderer
{
    // Render at `w`x`h` logical units, upscaled by `scale` for a crisp export, on an OPAQUE background
    // (the offscreen canvas is transparent by default, which reads as a dirty/half-missing image).
    public static byte[] Render(CandleChartDrawable drawable, int w, int h, Color? background = null, float scale = 2f)
    {
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (scale < 1f) scale = 1f;

        int pxW = (int)(w * scale);
        int pxH = (int)(h * scale);

        // displayScale 1 ⇒ 1 logical unit = 1 pixel. Fill the WHOLE pixel bitmap, then scale the canvas
        // by `scale` so the drawable's logical (w, h) maps edge-to-edge onto (pxW, pxH). (Passing `scale`
        // as displayScale instead does NOT transform the canvas here, so the chart drew into the top-left
        // (w, h) sub-region and left the rest blank.)
        using var ctx = new SkiaBitmapExportContext(pxW, pxH, 1f);
        var canvas = ctx.Canvas;

        canvas.FillColor = background ?? Colors.Black;
        canvas.FillRectangle(0, 0, pxW, pxH);

        canvas.SaveState();
        canvas.Scale(scale, scale);
        drawable.Draw(canvas, new RectF(0, 0, w, h));
        canvas.RestoreState();

        using var ms = new MemoryStream();
        ctx.Image.Save(ms);   // PNG by default
        return ms.ToArray();
    }
}
