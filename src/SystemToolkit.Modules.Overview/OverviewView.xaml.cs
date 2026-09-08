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
        // 导出对话框与用户提示经 View 注入（审查 🔴-1：VM 不直接依赖 SaveFileDialog/MessageBox）
        _vm.PickSavePath = () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出概览报告",
                FileName = $"SystemToolkit-概览报告-{DateTime.Now:yyyyMMdd-HHmm}",
                Filter = "Markdown 文档 (*.md)|*.md|所有文件 (*.*)|*.*",
                DefaultExt = ".md",
            };
            return dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true ? dialog.FileName : null;
        };
        _vm.NotifyUser = (message, title) => System.Windows.MessageBox.Show(
            message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        Loaded += async (_, _) => await _vm.ActivateAsync();
        Unloaded += (_, _) => _vm.Pause();
    }
}
