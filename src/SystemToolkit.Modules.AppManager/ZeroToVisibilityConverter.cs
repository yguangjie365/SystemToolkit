using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SystemToolkit.Modules.AppManager;

/// <summary>整数为 0 时显示空态提示（Count == 0 → Visible）。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    /// <summary>XAML 资源引用所需的默认构造函数。</summary>
    public ZeroToVisibilityConverter()
    {
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
