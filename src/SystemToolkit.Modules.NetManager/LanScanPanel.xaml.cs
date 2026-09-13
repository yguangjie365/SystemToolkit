using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 局域网扫描 Tab 面板（NET-6）：DataContext 由父视图注入 <c>{Binding Lan}</c>。
/// code-behind 仅两件事——事件流折叠（NetManagerView 日志面板同款）与「定位冲突行」
/// （VM 无法触达 ListView 滚动，经 <see cref="LanScanTabViewModel.LocateRequested"/> 桥接）。
/// </summary>
public partial class LanScanPanel : UserControl
{
    private LanScanTabViewModel? _vm;

    public LanScanPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookVm();
    }

    private void HookVm()
    {
        if (_vm is not null)
        {
            _vm.LocateRequested -= ScrollToIp;
        }

        _vm = DataContext as LanScanTabViewModel;
        if (_vm is not null)
        {
            _vm.LocateRequested += ScrollToIp;
            // 导出保存路径由 View 选，VM 只负责生成内容与写盘（互传历史导出的同款分工）
            _vm.PickExportPath = PickExportPath;
        }
    }

    private void ScrollToIp(string ip)
    {
        LanDeviceRow? row = _vm?.Rows.FirstOrDefault(r =>
            string.Equals(r.Ip, ip, StringComparison.Ordinal) && r.Kind != LanRowKind.ConflictPeer);
        if (row is not null)
        {
            DeviceList.SelectedItem = row;
            DeviceList.ScrollIntoView(row);
        }
    }

    /// <summary>事件流折叠（NetManagerView 日志面板同款纪律：解析期 IsChecked=True 会提前触发，判空）。</summary>
    private void OnEventsToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle && EventsHost is not null)
        {
            bool expanded = toggle.IsChecked == true;
            EventsHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = expanded ? "▾ 事件流（最近 50 条）" : "▸ 事件流（最近 50 条）";
        }
    }

    /// <summary>告警推送区折叠（B3-③，同款纪律）。</summary>
    private void OnAlertToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle && AlertHost is not null)
        {
            bool expanded = toggle.IsChecked == true;
            AlertHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = expanded ? "▾ 告警推送" : "▸ 告警推送";
        }
    }

    /// <summary>导出事件流的保存路径（取消返回 null）。默认落到「下载」目录，文件名带时间戳。</summary>
    private string? PickExportPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出局域网事件",
            FileName = suggestedFileName,
            DefaultExt = ".csv",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            InitialDirectory = SystemToolkit.Core.Utilities.UserFolders.GetDownloadsFolder(),
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }
}
