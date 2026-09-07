using System.Windows;
using System.Windows.Controls;

namespace SystemToolkit.Modules.DriverManager;

/// <summary>
/// 备份向导首步（纯代码窗口，与 SoftwareEditWindow 同款模式）：
/// 范围（全部第三方 / 仅勾选）+ 目标目录 → Show() 返回 (包名列表, 目录) 或 null（取消）。
/// 目标目录由主进程创建（无需提权）；pnputil /export-driver 本身经提权通道执行。
/// </summary>
public sealed class DriverBackupWindow : Window
{
    private readonly RadioButton _scopeAll;
    private readonly TextBox _destBox;
    private readonly int _thirdPartyCount;
    private readonly int _selectedCount;

    private DriverBackupWindow(int thirdPartyCount, int selectedCount)
    {
        _thirdPartyCount = thirdPartyCount;
        _selectedCount = selectedCount;

        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Font_Body");
        Title = "备份驱动向导";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var scopeAll = new RadioButton
        {
            Content = $"全部第三方驱动（{thirdPartyCount} 个）——收件箱驱动不在导出范围",
            GroupName = "Scope",
            IsChecked = thirdPartyCount > 0,
            IsEnabled = thirdPartyCount > 0,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var scopeSelected = new RadioButton
        {
            Content = $"仅勾选的驱动包（{selectedCount} 个）",
            GroupName = "Scope",
            IsChecked = thirdPartyCount == 0 && selectedCount > 0,
            IsEnabled = selectedCount > 0,
            Margin = new Thickness(0, 0, 0, 10),
        };
        _scopeAll = scopeAll;

        var destLabel = new TextBlock { Text = "目标目录（将自动创建时间戳子目录）", Margin = new Thickness(0, 0, 0, 4) };
        _destBox = new TextBox
        {
            Text = @"D:\DriverBackups",
            Margin = new Thickness(0, 0, 0, 10),
        };

        var hint = new TextBlock
        {
            Text = "导出经 pnputil /export-driver（官方机制），执行时将弹出 UAC 提权确认；\n"
                 + "产物为每个驱动包一个子目录，可直接用于重装后恢复。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var ok = new Button { Content = "开始备份（需提权）", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 4, 14, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_destBox.Text))
            {
                _ = MessageBox.Show(this, "目标目录不能为空。", "备份", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "备份范围", Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(scopeAll);
        root.Children.Add(scopeSelected);
        root.Children.Add(destLabel);
        root.Children.Add(_destBox);
        root.Children.Add(hint);
        root.Children.Add(buttons);

        Content = root;
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

        string destRoot = win._destBox.Text.Trim().TrimEnd('\\');
        string destDir = $"{destRoot}\\{DateTime.Now:yyyy-MM-dd_HHmm}";
        bool all = win._scopeAll.IsChecked == true;
        IReadOnlyList<string> names = pickNames(all, destDir);
        return names.Count == 0 ? null : (names, destDir, all);
    }
}
