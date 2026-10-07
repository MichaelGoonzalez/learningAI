using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HandRaise.Desktop.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public Visibility TrueValue { get; set; } = Visibility.Visible;
    public Visibility FalseValue { get; set; } = Visibility.Collapsed;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = false;
        if (value is bool b)
        {
            flag = b;
        }
        else if (value is int i)
        {
            flag = i > 0;
        }
        else if (value is string s)
        {
            flag = !string.IsNullOrWhiteSpace(s);
        }
        else if (value != null)
        {
            flag = true;
        }

        if (parameter is string paramStr && string.Equals(paramStr, "inverse", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? TrueValue : FalseValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility vis)
        {
            bool result = vis == TrueValue;
            if (parameter is string paramStr && string.Equals(paramStr, "inverse", StringComparison.OrdinalIgnoreCase))
            {
                result = !result;
            }
            return result;
        }
        return false;
    }
}
