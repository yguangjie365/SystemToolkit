using System.Windows;
using System.Windows.Controls;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 网络管理视图：5 Tab（设置/诊断/修复/优化/局域网扫描）+ 底部共享日志面板。
/// Loaded 完成组合根接线（确认回调 + 首屏数据，幂等）；Tab 切换由 code-behind 控制面板可见性
/// （AppManager 同款机制——不改布局结构，无响应式重排）。
/// </summary>
public partial class NetManagerView : UserControl
{
    private NetManagerViewModel Vm => (NetManagerViewModel)DataContext;

    private bool _loaded;

    public NetManagerView(NetManagerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
        // 审查 O7（2026-09-10）：切页卸载/关窗时取消持续 ping（循环与 VM 常驻泄漏）；NET-6 同款收口自动监控
        Unloaded += (_, _) =>
        {
            (DataContext as NetManagerViewModel)?.Diagnostics.CancelPing();
            (DataContext as NetManagerViewModel)?.Lan.CancelMonitor();
        };
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
            Settings.ConfirmRequest = Vm.ConfirmRequest;
            Repair.ConfirmRequest = Vm.ConfirmRequest;
            Optimize.ConfirmRequest = Vm.ConfirmRequest;
            await Vm.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
                SystemToolkit.Core.Logging.LogLevel.Error, "netmanager", "网络页初始化失败：" + ex.Message));
        }
    }

    private NetSettingsTabViewModel Settings => Vm.Settings;

    private NetRepairTabViewModel Repair => Vm.Repair;

    private NetOptimizeTabViewModel Optimize => Vm.Optimize;

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }
            && int.TryParse(tag, out int index)
            && DataContext is NetManagerViewModel vm)
        {
            vm.SelectedTabIndex = index;
            ShowPanel(index);
        }
    }

    /// <summary>操作日志折叠开关（审查 🔴 采纳，2026-09-09；与 FileBackup 同款）。
    /// ⚠️ IsChecked="True" 会在 InitializeComponent 解析期触发 Checked——此时 LogHost 尚未赋值，必须判空。</summary>
    private void OnLogToggleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle && LogHost is not null)
        {
            bool expanded = toggle.IsChecked == true;
            LogHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = expanded ? "▾ 操作日志" : "▸ 操作日志";
        }
    }

    /// <summary>面板可见性切换（仅改 Visibility，不动布局结构）。</summary>
    private void ShowPanel(int index)
    {
        SettingsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        RepairPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        OptimizePanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        LanPanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
    }
}
