using System.Globalization;
using System.Windows.Data;

namespace TradingStudio.UI.Converters;

/// <summary>
/// bool → ▶ / ⏸ 图标文本
/// </summary>
public class RunningToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "⏸" : "▶";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → "暂停" / "启动实时"
/// </summary>
public class RunningToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "暂停" : "启动实时";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
