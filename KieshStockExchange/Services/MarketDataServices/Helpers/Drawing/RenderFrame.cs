using KieshStockExchange.Helpers;

namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// One paint's worth of committed geometry: pane rectangles, axis ranges, and every data<->pixel
// transform. Built exactly once per Draw(); renderers receive it as a parameter; hit-testing reads
// the last-built instance. Immutable — a frame never changes after construction.
internal sealed class RenderFrame
{
    public RectF Plot { get; }
    public RectF VolRect { get; }
    public RectF MoodRect { get; }
    public double YMin { get; }
    public double YMax { get; }
    public DateTime TMin { get; }
    public DateTime TMax { get; }
    public double SpanSec { get; }        // precomputed once per paint, exactly as Draw() does
    public PriceScaleMode Mode { get; }   // captured at build — consumers stop reading the live property
    public TimeSpan Bucket { get; }       // Viewport.Bucket snapshot
    public CurrencyType Currency { get; }
    // Live price at paint time — nullable + init-only so no existing ctor call / test fixture breaks
    // (a new ctor param would). Lets a drawing renderer (the Alert level) react to the current price.
    public decimal? CurrentPrice { get; init; }
    private readonly IScaleTransform _scale;   // stateless; injected from the drawable

    public RenderFrame(RectF plot, RectF volRect, RectF moodRect,
        double yMin, double yMax, DateTime tMin, DateTime tMax, double spanSec,
        PriceScaleMode mode, TimeSpan bucket, CurrencyType currency, IScaleTransform scale)
    {
        Plot = plot; VolRect = volRect; MoodRect = moodRect;
        YMin = yMin; YMax = yMax; TMin = tMin; TMax = tMax; SpanSec = spanSec;
        Mode = mode; Bucket = bucket; Currency = currency; _scale = scale;
    }

    // Pre-first-paint stand-in reproducing the old cache-field initializers:
    // rects default, YMin = 0, YMax = 1.0, TMin = TMax = default.
    public static readonly RenderFrame Empty = new(
        default, default, default, 0.0, 1.0, default, default, 0.0,
        default, default, default, new RegularScaleTransform());

    // Early-return paths in Draw() commit the NEW pane rects while KEEPING the previous ranges —
    // mirrors the old seven-field cache, where the rect fields were written before the no-data /
    // zero-span bailouts but the range fields were not.
    public RenderFrame WithRects(RectF plot, RectF vol, RectF mood)
        => new(plot, vol, mood, YMin, YMax, TMin, TMax, SpanSec, Mode, Bucket, Currency, _scale)
        { CurrentPrice = CurrentPrice };

    // ---- forward transforms (RENDER form — the old X/Y closures: cast then subtract in float) ----
    public float MapX(DateTime utc)
        => Plot.Left + (float)(((utc - TMin).TotalSeconds / SpanSec) * Plot.Width);

    public float MapY(double price)
        => Plot.Bottom - (float)(ChartGeometry.PriceToFrac(price, YMin, YMax, Mode) * Plot.Height);

    // The price<->pixel seam the drawing renderers route through (ExtendedLine/Rect/Ellipse/Arrow).
    public float ScaleY(decimal price)
        => _scale.PriceToPixelY(price, Plot, YMin, YMax, Mode);

    // ---- forward transforms (HIT form — subtract in double, cast LAST; deliberately different in
    //      the low bits from MapY, preserved verbatim from the old hit-test helpers) ----
    public float HitPriceToPixelY(decimal price)
        => (float)(Plot.Bottom - ChartGeometry.PriceToFrac((double)price, YMin, YMax, Mode) * Plot.Height);

    public float TimeToPixelX(DateTime t)
    {
        if (TMax <= TMin) return Plot.Left;
        double frac = (t - TMin).TotalSeconds / (TMax - TMin).TotalSeconds;
        return (float)(Plot.Left + frac * Plot.Width);
    }

    // ---- inverse transforms (verbatim bodies of the drawable's public PixelToPrice/PixelToTime) ----
    public decimal? PixelToPrice(float yInControl)
    {
        if (Plot.Height <= 0 || YMax <= YMin) return null;
        if (yInControl < Plot.Top || yInControl > Plot.Bottom) return null;
        double frac = (Plot.Bottom - yInControl) / (double)Plot.Height;
        double price = ChartGeometry.FracToPrice(frac, YMin, YMax, Mode);
        return (decimal)price;
    }

    public DateTime PixelToTime(float xInControl)
    {
        if (Plot.Width <= 0 || TMax <= TMin) return TMin;
        double frac = (xInControl - Plot.Left) / (double)Plot.Width;
        frac = Math.Clamp(frac, 0.0, 1.0);
        var span = TMax - TMin;
        return TMin.AddTicks((long)(span.Ticks * frac));
    }
}
