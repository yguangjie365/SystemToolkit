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
