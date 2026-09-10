using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>bool → 画刷（启用=成功色/停用=静音色），规则列表的启停徽标用。</summary>
public sealed class EnabledToBrushConverter : IValueConverter
{
    // 🔴 审查 2026-09-11（🔴-3）：原为 static readonly —— 类型级初始化**只执行一次**，
    // 主题切换后徽标颜色永不跟随（Claude→Nvidia 后浅色主题的成功/静音色直接压在近黑底上）。
    // 改为「按主题 id 缓存」：既跟随主题，又避免每次 Convert 都走一遍资源查找。
    private static string? _cachedThemeId;
    private static Brush _enabledBrush = ThemeBrush.Find("Brush_Success", "#047857");
    private static Brush _disabledBrush = ThemeBrush.Find("Brush_TextMuted", "#6B7280");

    private static void EnsureThemeCache()
    {
        string current = ThemeManager.CurrentThemeId;
        if (_cachedThemeId == current)
        {
            return;
        }

        // 走主题资源（主题未就绪时回退 hex）——直接 new SolidColorBrush 会让徽标脱离主题机制
        _enabledBrush = ThemeBrush.Find("Brush_Success", "#047857");
        _disabledBrush = ThemeBrush.Find("Brush_TextMuted", "#6B7280");
        _cachedThemeId = current;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        EnsureThemeCache();
        return value is true ? _enabledBrush : _disabledBrush;
    }

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
