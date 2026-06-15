using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TradingStudio.Terminal.Converters;

/// <summary>集合为空 → Collapsed, 非空 → Visible</summary>
public class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
