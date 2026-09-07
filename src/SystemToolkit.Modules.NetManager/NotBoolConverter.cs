using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SystemToolkit.Modules.NetManager;

/// <summary>bool 取反（双向）：DHCP/静态互斥 Radio 各绑一极时使用（Convert 与 ConvertBack 同为取反）。</summary>
public sealed class NotBoolConverter : IValueConverter
{
    public static readonly NotBoolConverter Instance = new();

    private NotBoolConverter()
    {
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}

/// <summary>整数为 0 时显示空态提示（Count == 0 → Visible）。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public static readonly ZeroToVisibilityConverter Instance = new();

    /// <summary>XAML 资源引用所需的默认构造函数。</summary>
    public ZeroToVisibilityConverter()
    {
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
