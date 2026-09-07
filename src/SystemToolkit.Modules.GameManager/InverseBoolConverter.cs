using System.Globalization;
using System.Windows.Data;

namespace SystemToolkit.Modules.GameManager;

/// <summary>bool 取反 → Visibility 语义配套使用（true 隐藏 / false 显示，配 DataTrigger 或直接绑定 Visibility）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    private InverseBoolConverter()
    {
    }

    /// <summary>true → Collapsed / false → Visible（用于"未安装"空态与正常态互斥显示）。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
