using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SystemToolkit.Modules.Overview;

/// <summary>null/空白 string → Collapsed，非空 string → Visible：概览卡温度徽章用。
/// （审查 🟠-1 澄清：实现按字符串空白判定；非 string 类型经 as 转换为 null → Collapsed。）</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
