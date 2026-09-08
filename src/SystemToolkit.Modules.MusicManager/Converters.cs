using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>bool 取反（转 bool；绑定 Visibility 场景请配 BoolToVis 转换器链或用专用转换器）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value as bool? ?? false);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value as bool? ?? false);
}

/// <summary>bool → Visibility（false = Collapsed，true = Visible）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool 取反 → Visibility（true → Collapsed；「引擎未就绪」「无歌词」空态用）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 字符串相等 → Visible，否则 Collapsed（参数 = 期望值；音量图标三态切换用，审查 P0-1）。
/// </summary>
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 集合计数 0（或 null）→ Visible，否则 Collapsed（列表空态提示用，审查 P0-2）。
/// 直接绑 <c>ItemsSource</c> 的 Count，无需在 VM 额外暴露 IsEmpty 属性。
/// </summary>
public sealed class EmptyCollectionToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>string 非空 → Visible，空/null → Collapsed（状态行、警告条用）。</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
