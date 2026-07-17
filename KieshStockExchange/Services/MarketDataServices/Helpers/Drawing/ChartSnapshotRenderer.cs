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

        // SkiaBitmapExportContext(pixelW, pixelH, displayScale): the canvas maps logical units × scale
        // → pixels, so we draw at logical (w, h) and get a scale×-resolution bitmap.
        using var ctx = new SkiaBitmapExportContext((int)(w * scale), (int)(h * scale), scale);
        var canvas = ctx.Canvas;

        canvas.FillColor = background ?? Colors.Black;
        canvas.FillRectangle(0, 0, w, h);

        drawable.Draw(canvas, new RectF(0, 0, w, h));

        using var ms = new MemoryStream();
        ctx.Image.Save(ms);   // PNG by default
        return ms.ToArray();
    }
}
