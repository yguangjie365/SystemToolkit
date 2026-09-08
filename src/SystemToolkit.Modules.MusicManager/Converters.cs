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
/// 比例换算（审查 P1-7/8）：把绑定到的实际尺寸乘以参数比例，用于"跟随窗口缩放"的装饰尺寸
/// （黑胶圆盘、现代模板封面卡），替代原先写死的 540 / 220 像素。
/// 参数形式："0.62" 或 "0.62|300|520"（比例|最小值|最大值）。
/// ⚠️ 不能用逗号分隔：XAML MarkupExtension 会把逗号当作名/值对分隔符（MC3042）。
/// </summary>
public sealed class RatioConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double source || source <= 0)
        {
            return 0d;
        }

        string[] parts = (parameter as string ?? "1").Split('|');
        double ratio = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : 1d;
        double result = source * ratio;
        if (parts.Length > 2)
        {
            double min = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double mn) ? mn : 0d;
            double max = double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double mx) ? mx : double.MaxValue;
            result = Math.Clamp(result, min, max);
        }

        return result;
    }

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
