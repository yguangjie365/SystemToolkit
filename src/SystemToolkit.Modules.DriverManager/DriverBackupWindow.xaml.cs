using System.Windows;
using System.Windows.Controls;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.DriverManager;

/// <summary>
/// 备份向导首步（XAML 版，与 SoftwareEditWindow 同款模式）：范围 + 目标目录 → Show() 返回
/// (包名列表, 目录) 或 null（取消）。目标目录由主进程创建（无需提权）；pnputil /export-driver 经提权通道执行。
/// 本窗口不设 DataContext（与 SoftwareEditWindow 同款），故范围计数经构造参数注入。
/// （XAML 本身完全可以绑运行时数据，此处是"没设 DataContext"而非"XAML 做不到"。）
/// </summary>
public sealed partial class DriverBackupWindow : Window
{
    private DriverBackupWindow(int thirdPartyCount, int selectedCount)
    {
        InitializeComponent();
        // 标题栏跟随主题明暗（共享接线器：句柄就绪套一次 + 主题切换跟随 + 关闭退订）
        TitleBarThemeWiring.Attach(this);

        ScopeAllRadio.Content = $"全部第三方驱动（{thirdPartyCount} 个）——收件箱驱动不在导出范围";
        ScopeAllRadio.IsChecked = thirdPartyCount > 0;
        ScopeAllRadio.IsEnabled = thirdPartyCount > 0;

        ScopeSelectedRadio.Content = $"仅勾选的驱动包（{selectedCount} 个）";
        ScopeSelectedRadio.IsChecked = thirdPartyCount == 0 && selectedCount > 0;
        ScopeSelectedRadio.IsEnabled = selectedCount > 0;

        HintText.Text = "导出经 pnputil /export-driver（官方机制），执行时将弹出 UAC 提权确认；\n"
                      + "产物为每个驱动包一个子目录，可直接用于重装后恢复。";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        string dest = DestBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dest))
        {
            _ = MessageBox.Show(this, "目标目录不能为空。", "备份", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 审查 🟠-5 采纳（2026-09-09）：仅查非空时，非法字符/过长路径会拖到备份开始后才失败；
        // 在此用 GetFullPath 前置校验（非法字符/格式错误会抛），提前给出可读提示
        try
        {
            _ = System.IO.Path.GetFullPath(dest);
        }
        catch (Exception ex)
        {
            _ = MessageBox.Show(this, $"目标目录路径无效：{ex.Message}\n请改用合法路径（例如 D:\\DriverBackup）。",
                "备份", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    /// <summary>返回 null = 取消；否则返回 (包名列表, 目标目录, 是否"全部第三方"范围)。pickNames：all=全部第三方 / false=仅勾选。</summary>
    public static (IReadOnlyList<string> Names, string DestDir, bool AllThirdParty)? Show(
        Window? owner, int thirdPartyCount, int selectedCount, Func<bool, string, IReadOnlyList<string>> pickNames)
    {
        // 🟠 v11~v14 后续批次：无可用范围时提前拒绝。否则两个 RadioButton 虽都已禁用，
        // 用户仍可点「开始备份（需提权）」→ pickNames 内的 Directory.CreateDirectory 先建出
        // 一个空时间戳目录，再因 names.Count == 0 返回 null（上层视作"用户取消"）⇒
        // 用户零反馈、磁盘却多一个空目录。此处返回 null 后上层落一条"向导取消，未启动"日志。
        if (thirdPartyCount == 0 && selectedCount == 0)
        {
            return null;
        }

        var win = new DriverBackupWindow(thirdPartyCount, selectedCount)
        {
            Owner = owner,
        };
        if (win.ShowDialog() != true)
        {
            return null;
        }

        string destRoot = win.DestBox.Text.Trim().TrimEnd('\\');
        string destDir = $"{destRoot}\\{DateTime.Now:yyyy-MM-dd_HHmm}";
        bool all = win.ScopeAllRadio.IsChecked == true;
        IReadOnlyList<string> names = pickNames(all, destDir);
        return names.Count == 0 ? null : (names, destDir, all);
    }
}
