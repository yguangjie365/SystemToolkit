using System.Windows.Controls;

namespace SystemToolkit.Modules.Overview;

/// <summary>
/// 本机概览页视图。数据绑定到 <see cref="OverviewViewModel"/>（由 Shell 的 DI 提供，页面级单例）。
/// 生命周期：Loaded → ActivateAsync（启动 2s 刷新）；Unloaded → Pause（立即停止）。
/// 窗口失焦由 MainWindow 统一调 Pause / ActivateAsync。
/// </summary>
public partial class OverviewView : UserControl
{
    private readonly OverviewViewModel _vm;

    public OverviewView(OverviewViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.ActivateAsync();
        Unloaded += (_, _) => _vm.Pause();
    }
}
