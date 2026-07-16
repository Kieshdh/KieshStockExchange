using System.IO;
using Microsoft.Maui.Graphics.Skia;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// UP-CORE: renders a CandleChartDrawable offscreen to PNG bytes (deterministic, headless). A later
// phase wires the button/clipboard/toast. Uses Skia's offscreen canvas — CandleChartDrawable lives in
// the enclosing ...MarketDataServices namespace, so it resolves without an extra using.
//
// NOTE: this depends on Microsoft.Maui.Graphics.Skia (added to the client csproj) and on the full
// drawable, so it is intentionally NOT part of the headless unit-test link set.
public static class ChartSnapshotRenderer
{
    public static byte[] Render(CandleChartDrawable drawable, int w, int h)
    {
        using var ctx = new SkiaBitmapExportContext(w, h, 1f);
        drawable.Draw(ctx.Canvas, new RectF(0, 0, w, h));
        using var ms = new MemoryStream();
        ctx.Image.Save(ms);   // PNG by default
        return ms.ToArray();
    }
}
