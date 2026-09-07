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
        Vm.ConfirmRequest = (title, message) =>
            System.Windows.MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK;
        Vm.Desktop.ConfirmRequest = Vm.ConfirmRequest;
        Vm.Desktop.PickFiles = PickFiles;
        await Vm.LoadAsync().ConfigureAwait(true);
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

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
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

    private void OnDiscoveredDeviceDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (sender is ListBox { SelectedItem: DiscoveredDeviceRowVm device })
        {
            _ = Vm.Desktop.SendFilesToAsync(device.Model.IPAddress.ToString(), device.Model.TransferPort, files);
        }
        else
        {
            System.Windows.MessageBox.Show("请先选中目标设备行，再拖入文件。", "文件互传",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnKnownPeerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (sender is ListBox { SelectedItem: KnownPeerRowVm peer }
            && System.Net.IPAddress.TryParse(peer.Ip, out _)
            && peer.Port is >= 1 and <= 65535)
        {
            _ = Vm.Desktop.SendFilesToAsync(peer.Ip, peer.Port, files);
        }
        else
        {
            System.Windows.MessageBox.Show("请先选中目标设备行（IP/端口需合法），再拖入文件。", "文件互传",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
