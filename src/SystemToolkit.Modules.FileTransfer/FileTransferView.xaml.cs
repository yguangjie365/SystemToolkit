using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>
/// 文件互传视图：2 Tab（电脑互传 / 手机通道占位）+ 底部共享日志面板。
/// Loaded 完成组合根接线（确认/文件选择回调 + 配置历史加载，幂等）；
/// 设备行支持拖拽发送（DataObject FileDrop → SendFilesToAsync）。
/// </summary>
public partial class FileTransferView : UserControl
{
    private FileTransferViewModel Vm => (FileTransferViewModel)DataContext;

    private bool _loaded;

    public FileTransferView(FileTransferViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        // 审查 O9（2026-09-10）：async void 不受命令 catch 守卫覆盖，异常会直冲 Dispatcher → 整体兜底并落日志
        try
        {
            Vm.ConfirmRequest = (title, message) =>
                System.Windows.MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                == MessageBoxResult.OK;
            Vm.Desktop.ConfirmRequest = Vm.ConfirmRequest;
            // 接收确认走**信息完整的专用对话框**（2026-09-13 批次 P1 ⑦）：
            // 用户要判断"接不接、会不会覆盖东西"，只给 IP + 文件名是让人盲签。
            // 窗口在 View 里创建（VM 不弹窗，便于无 UI 单测）；Owner 指向当前窗口保证居中与模态归属。
            Vm.Desktop.ConfirmTransferRequest = request =>
            {
                var dialog = new ReceiveConfirmWindow(request, Vm.Desktop.ReceiveConfirmTimeoutSeconds)
                {
                    Owner = Window.GetWindow(this),
                };
                dialog.ShowDialog();
                return dialog.Decision; // 关窗/Esc 都收场为「拒绝」（窗口默认值）
            };
            Vm.Desktop.PickFiles = PickFiles;
            // 历史导出（P3 ⑮）：保存路径由 View 选，VM 只负责生成内容与写盘
            Vm.Desktop.PickExportPath = PickExportPath;
            await Vm.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Error, "filetransfer", "互传页初始化失败：" + ex.Message));
        }
    }

    /// <summary>多选文件对话框（发送入口；取消返回 null）。</summary>
    private IReadOnlyList<string>? PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要发送的文件（可多选）",
            Filter = "所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : null;
    }

    /// <summary>导出历史的保存路径（取消返回 null）。默认落到「下载」目录，文件名带时间戳。</summary>
    private string? PickExportPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出传输历史",
            FileName = suggestedFileName,
            DefaultExt = ".csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            InitialDirectory = SystemToolkit.Core.Utilities.UserFolders.GetDownloadsFolder(),
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    private void OnPickReceiveDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择接收文件保存目录",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Vm.Desktop.ReceiveDirectory = dialog.FolderName;
        }
    }

    private void OnPickShareDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择共享给手机的目录",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Vm.Mobile.ShareDirectory = dialog.FolderName;
        }
    }

    private void OnOpenWebPage(object sender, RoutedEventArgs e)
    {
        string url = Vm.Mobile.UrlText;
        if (!string.IsNullOrWhiteSpace(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void OnOpenReceiveDirectory(object sender, RoutedEventArgs e)
    {
        string dir = Vm.Desktop.ReceiveDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            System.Windows.MessageBox.Show("接收目录不存在（启动服务后才会创建）。", "打开接收目录",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 审查 v5（🟡-12）：ArgumentList 逐参传递，替代手工引号拼接
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        psi.ArgumentList.Add(dir);
        Process.Start(psi);
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }
            && int.TryParse(tag, out int index))
        {
            Vm.SelectedTabIndex = index;
            DesktopPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            MobilePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── 拖拽发送（DataObject FileDrop → 统一发送入口） ──

    private void OnPeerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 拖到哪行就发到哪行（审查 🔴-3 采纳）：沿可视树向上找 ListBoxItem 取行 DataContext，
    /// 找不到再回退 SelectedItem——不再要求「先选中再拖」。
    /// ⚠️ 不要用 ItemsControl.ItemsControlFromItemContainer——它对容器内部元素返回 null
    /// （MusicManager 双击回归同款教训，2026-09-08 实证）。
    /// </summary>
    private static object? ResolveRowUnderMouse(object? source, ListBox list)
    {
        if (source is not DependencyObject start)
        {
            return list.SelectedItem;
        }

        DependencyObject d = start;
        while (d is not null)
        {
            if (d is ListBoxItem item)
            {
                return item.DataContext;
            }

            d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }

        return list.SelectedItem;
    }

    private void OnDiscoveredDeviceDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (sender is not ListBox list)
        {
            return;
        }

        if (ResolveRowUnderMouse(e.OriginalSource, list) is DiscoveredDeviceRowVm device)
        {
            _ = Vm.Desktop.SendFilesToAsync(device.Model.IPAddress.ToString(), device.Model.TransferPort, files);
        }
        else
        {
            System.Windows.MessageBox.Show("请把文件拖到目标设备行上，再松开发送。", "文件互传",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnKnownPeerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (sender is not ListBox list)
        {
            return;
        }

        if (ResolveRowUnderMouse(e.OriginalSource, list) is KnownPeerRowVm peer
            && System.Net.IPAddress.TryParse(peer.Ip, out _)
            && peer.Port is >= 1 and <= 65535)
        {
            _ = Vm.Desktop.SendFilesToAsync(peer.Ip, peer.Port, files);
        }
        else
        {
            System.Windows.MessageBox.Show("请把文件拖到目标已知设备行上（IP/端口需合法），再松开发送。", "文件互传",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
