namespace KieshStockExchange.Services.MarketDataServices.Helpers.Drawing;

// UP-CORE: the price<->pixel seam. Every NEW drawing renderer routes vertical mapping through this
// interface so a later phase can add Log/Percent/Indexed transforms without editing each renderer.
// UP-CORE ships only RegularScaleTransform (current behaviour); existing renderers keep their local
// closures (see below). PriceScaleMode lives in the enclosing ...Helpers namespace, so it resolves
// without an extra using.
public interface IScaleTransform
{
    float PriceToPixelY(decimal price, RectF plot, double yMin, double yMax, PriceScaleMode mode);
    decimal PixelToPrice(float y, RectF plot, double yMin, double yMax, PriceScaleMode mode);
}

// Linear/Log price scale — the identical behaviour the chart uses today. PriceToPixelY reproduces the
// render pass's Y closure EXACTLY (frac*height cast to float first, then subtracted from plot.Bottom),
// not the hit-test PriceToPixelY form (subtract-in-double, cast last), so new shapes land on the same
// pixels HLine/Trend/etc. paint on. PixelToPrice mirrors the drawable's PixelToPrice/FracToPrice.
public sealed class RegularScaleTransform : IScaleTransform
{
    public float PriceToPixelY(decimal price, RectF plot, double yMin, double yMax, PriceScaleMode mode)
        => plot.Bottom - (float)(PriceToFrac((double)price, yMin, yMax, mode) * plot.Height);

    public decimal PixelToPrice(float y, RectF plot, double yMin, double yMax, PriceScaleMode mode)
    {
        if (plot.Height <= 0f || yMax <= yMin) return 0m;
        double frac = (plot.Bottom - y) / (double)plot.Height;
        return (decimal)FracToPrice(frac, yMin, yMax, mode);
    }

    // Mirror of CandleChartDrawable.PriceToFrac / FracToPrice (ScaleMode-aware) so the seam renders
    // identically. Log maps equal ratios to equal pixels; Linear/Percent are plain linear.
    private static double PriceToFrac(double price, double lo, double hi, PriceScaleMode mode)
    {
        if (mode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return b <= a ? 0.0 : (Math.Log(Math.Max(price, 1e-9)) - a) / (b - a);
        }
        return hi <= lo ? 0.0 : (price - lo) / (hi - lo);
    }

    private static double FracToPrice(double frac, double lo, double hi, PriceScaleMode mode)
    {
        if (mode == PriceScaleMode.Logarithmic)
        {
            double a = Math.Log(Math.Max(lo, 1e-9)), b = Math.Log(Math.Max(hi, 1e-9));
            return Math.Exp(a + frac * (b - a));
        }
        return lo + frac * (hi - lo);
    }
}
