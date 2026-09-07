using System.Windows;
using Microsoft.Win32;
using SystemToolkit.Core.Backup.Contracts;

namespace SystemToolkit.Modules.FileBackup;

/// <summary>恢复对话框的用户选择结果。</summary>
/// <param name="TargetRoot">恢复目标根目录；<c>null</c> 表示恢复到原始位置。</param>
/// <param name="Policy">同名文件冲突处理策略。</param>
public sealed record RestoreChoice(string? TargetRoot, ConflictPolicy Policy);

/// <summary>
/// 恢复对话框（2026-09-07 补齐旧版能力）：恢复目标二选一 + 冲突策略四选一。
/// 只收集选择，不执行预演与恢复（由 VM 负责）。
/// </summary>
public partial class RestoreDialog : Window
{
    private readonly System.Windows.Documents.Run _originalPathRun;

    internal RestoreDialog(string summary, string originalPath)
    {
        InitializeComponent();
        SummaryText.Text = summary;
        _originalPathRun = OriginalPathRun;
        _originalPathRun.Text = string.IsNullOrWhiteSpace(originalPath) ? "（该规则无可用源路径）" : originalPath;
        OriginalRadio.IsEnabled = !string.IsNullOrWhiteSpace(originalPath);
        if (!OriginalRadio.IsEnabled)
        {
            CustomRadio.IsChecked = true;
        }
    }

    /// <summary>模态打开；确定返回选择结果，取消返回 null。</summary>
    public static RestoreChoice? Show(Window owner, string summary, string originalPath, string? defaultCustomPath = null)
    {
        var window = new RestoreDialog(summary, originalPath)
        {
            Owner = owner,
            Title = "恢复快照",
        };
        if (!string.IsNullOrWhiteSpace(defaultCustomPath))
        {
            window.TargetBox.Text = defaultCustomPath;
        }

        return window.ShowDialog() == true ? window.BuildChoice() : null;
    }

    private RestoreChoice BuildChoice()
    {
        ConflictPolicy policy = RenameRadio.IsChecked == true ? ConflictPolicy.Rename
            : OverwriteRadio.IsChecked == true ? ConflictPolicy.Overwrite
            : SkipRadio.IsChecked == true ? ConflictPolicy.Skip
            : ConflictPolicy.Rename;
        string? target = CustomRadio.IsChecked == true ? TargetBox.Text.Trim() : null;
        return new RestoreChoice(string.IsNullOrWhiteSpace(target) ? null : target, policy);
    }

    private void OnTargetModeChanged(object sender, RoutedEventArgs e)
    {
        // 🔴 XAML 构建期 IsChecked="True" 就会触发 Checked 事件，此时 TargetBox 尚未创建
        //（2026-09-08 真机「恢复点击无反应」根因：构造即 NRE 被命令吞掉）
        if (TargetBox is null)
        {
            return;
        }

        TargetBox.IsEnabled = CustomRadio.IsChecked == true;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择恢复目标目录" };
        if (dialog.ShowDialog(Owner) == true)
        {
            TargetBox.Text = dialog.FolderName;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (CustomRadio.IsChecked == true && string.IsNullOrWhiteSpace(TargetBox.Text))
        {
            System.Windows.MessageBox.Show(this, "请选择恢复目标目录（或改选「恢复到原始位置」）。",
                "恢复快照", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
