using System.Windows;
using System.Windows.Controls;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 网络管理视图：4 Tab（设置/诊断/修复/优化）+ 底部共享日志面板。
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
        Settings.ConfirmRequest = Vm.ConfirmRequest;
        Repair.ConfirmRequest = Vm.ConfirmRequest;
        Optimize.ConfirmRequest = Vm.ConfirmRequest;
        await Vm.LoadAsync().ConfigureAwait(true);
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

    /// <summary>面板可见性切换（仅改 Visibility，不动布局结构）。</summary>
    private void ShowPanel(int index)
    {
        SettingsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiagPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        RepairPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        OptimizePanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
    }
}
