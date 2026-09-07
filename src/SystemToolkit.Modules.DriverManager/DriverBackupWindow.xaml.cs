using System.Windows;
using System.Windows.Controls;

namespace SystemToolkit.Modules.DriverManager;

/// <summary>
/// 备份向导首步（XAML 版，与 SoftwareEditWindow 同款模式）：范围 + 目标目录 → Show() 返回
/// (包名列表, 目录) 或 null（取消）。目标目录由主进程创建（无需提权）；pnputil /export-driver 经提权通道执行。
/// 范围计数由构造参数注入（XAML 无法绑运行时计数）。
/// </summary>
public sealed partial class DriverBackupWindow : Window
{
    private readonly int _thirdPartyCount;
    private readonly int _selectedCount;

    private DriverBackupWindow(int thirdPartyCount, int selectedCount)
    {
        InitializeComponent();
        _thirdPartyCount = thirdPartyCount;
        _selectedCount = selectedCount;

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
        if (string.IsNullOrWhiteSpace(DestBox.Text))
        {
            _ = MessageBox.Show(this, "目标目录不能为空。", "备份", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    /// <summary>返回 null = 取消；否则返回 (包名列表, 目标目录, 是否"全部第三方"范围)。pickNames：all=全部第三方 / false=仅勾选。</summary>
    public static (IReadOnlyList<string> Names, string DestDir, bool AllThirdParty)? Show(
        Window? owner, int thirdPartyCount, int selectedCount, Func<bool, string, IReadOnlyList<string>> pickNames)
    {
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
