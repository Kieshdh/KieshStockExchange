using System.Globalization;

namespace KieshStockExchange.Helpers;
public class NotLastIndexConverter : IMultiValueConverter
{
    // values[0] = Index, values[1] = ItemsCount
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not int idx || values[1] is not int count) return false;
        return idx < (count - 1); // show divider unless it's the last item
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}