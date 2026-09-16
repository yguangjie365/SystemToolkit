using System.Globalization;
using System.Windows.Data;

namespace SystemToolkit.Modules.FileBackup;

// 🟠 v18-🟠-1（2026-09-16）：`EnabledToBrushConverter` 已删除。
// 它绑的是 RuleRowVm.Enabled——getter-only 派生属性、不实现 INPC ⇒ 主题切换时绑定源不变化
// ⇒ `Convert` 不会重跑 ⇒ 徽标颜色停在旧主题画刷（深色主题下浅色成功色压近黑底几乎不可见）。
// 「改为每次直取」并不能解决该问题（根本不会被再次调用）；正解 = 视图侧
// `Style` + `DataTrigger` + `{DynamicResource}`，由主题字典驱动刷新。见 FileBackupView.xaml 的启停徽标。

/// <summary>bool 取反（双向）：自定义备份根输入框随「使用全局备份根」反向启用。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}
