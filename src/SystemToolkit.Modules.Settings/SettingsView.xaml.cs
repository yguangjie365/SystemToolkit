using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SystemToolkit.Modules.Settings;

/// <summary>
/// 设置页（2026-09-07 建立）：左分组 + 右内容。当前仅「备份」分区可用，
/// 「通用」占位禁用。目录选择经 PickFolder 回调注入（VM 不依赖 Win32 对话框）。
/// </summary>
public partial class SettingsView : UserControl
{
    private SettingsViewModel Vm => (SettingsViewModel)DataContext;

    private bool _loaded;

    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        Vm.PickFolder = title =>
        {
            var dialog = new OpenFolderDialog { Title = title };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
        };
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 目前只有一个可用分区；后续新增分区在此切换可见性
        if (BackupSection is null)
        {
            return;
        }

        string tag = (SectionList.SelectedItem as ListBoxItem)?.Tag as string ?? "backup";
        BackupSection.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;
    }
}
