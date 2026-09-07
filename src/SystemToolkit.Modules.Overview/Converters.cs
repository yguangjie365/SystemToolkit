using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SystemToolkit.Modules.Overview;

/// <summary>null → Collapsed（非 null → Visible）：概览卡温度徽章用。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
