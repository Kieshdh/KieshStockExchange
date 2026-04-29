using System.Globalization;

namespace KieshStockExchange.Helpers;

public sealed class DepthRatioToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            _ => 0d
        };

        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) ratio = 0d;
        if (ratio < 0d) ratio = 0d;
        if (ratio > 1d) ratio = 1d;

        double maxWidth = 0d;
        if (parameter is not null)
        {
            try { maxWidth = System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture); }
            catch { maxWidth = 0d; }
        }

        return ratio * maxWidth;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
