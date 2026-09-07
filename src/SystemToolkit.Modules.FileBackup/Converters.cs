using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>bool → 画刷（启用=成功色/停用=静音色），规则列表的启停徽标用。</summary>
public sealed class EnabledToBrushConverter : IValueConverter
{
    // 🔴 走主题资源（主题未就绪时回退 hex）——直接 new SolidColorBrush 会让徽标脱离主题机制
    public static readonly Brush EnabledBrush = ThemeBrush.Find("Brush_Success", "#047857");
    public static readonly Brush DisabledBrush = ThemeBrush.Find("Brush_TextMuted", "#6B7280");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? EnabledBrush : DisabledBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool 取反（双向）：自定义备份根输入框随「使用全局备份根」反向启用。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}
