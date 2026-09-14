using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>bool → 画刷（启用=成功色/停用=静音色），规则列表的启停徽标用。</summary>
public sealed class EnabledToBrushConverter : IValueConverter
{
    // 🟠 C-🟠-2 v11~v14 后续批次：上一版「按主题 id 缓存」**实际无效**。WPF 只在
    // **绑定源变化**时重新调用 Convert；本转换器的唯一消费点绑的是 RuleRowVm.Enabled ——
    // 它是 getter-only 派生属性、不实现 INotifyPropertyChanged ⇒ Convert 只在首次绑定时
    // 执行一次。因此缓存永远等不到第二次调用：切主题后徽标颜色**停留在旧主题画刷**
    //（深色主题下浅色成功色压在近黑底上几乎不可见），直到 ReloadRules() 重建行 VM 才跟上。
    // 原注释「既跟随主题」与实现不符 ⇒ 改为每次直取（开销仅一次字典查找；Convert 的调用
    // 频率极低，只在绑定重新求值时）。
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? ThemeBrush.Find("Brush_SuccessText", "#047857")
            : ThemeBrush.Find("Brush_TextMuted", "#6B7280");

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
