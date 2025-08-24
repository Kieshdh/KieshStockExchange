using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace KieshStockExchange.Helpers;

public class EnumToIndexConverter : IValueConverter
{
    // Set this from XAML to the enum Type you’re mapping (e.g., typeof(TableType))
    public Type EnumType { get; set; } = typeof(Enum);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || !EnumType.IsEnum) return -1;

        var names = Enum.GetNames(EnumType); // order of declaration
        var name = Enum.GetName(EnumType, value);
        return Array.IndexOf(names, name);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int idx || !EnumType.IsEnum) return BindableProperty.UnsetValue;

        var values = Enum.GetValues(EnumType);
        if (idx < 0 || idx >= values.Length) return BindableProperty.UnsetValue;

        return values.GetValue(idx)!;
    }
}