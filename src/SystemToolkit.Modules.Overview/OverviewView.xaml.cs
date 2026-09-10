using System.Windows.Controls;
using System.Windows;

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

        // 2026-09-10：GridView 末列自适应填充——固定列宽不足视口时 GridView 会自动补一个
        // 空白表头列（现象：表头 6 列、内容 5 列）。让「大小」列吃掉剩余宽度即可消除。
        InstalledAppsList.SizeChanged += OnAppsListSizeChanged;

        // 但 VM 构造/依赖解析等边界异常会在此处逃逸成未处理异常（进程级崩溃）
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.ActivateAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Overview] 激活失败：{ex.Message}");
                System.Windows.MessageBox.Show(
                    $"概览页启动失败：{ex.Message}", "本机概览",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        };
        Unloaded += (_, _) => _vm.Pause();
    }

    private void OnAppsListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView list
            || list.View is not System.Windows.Controls.GridView grid
            || grid.Columns.Count == 0)
        {
            return;
        }

        const double MinLastColumn = 100;
        double scrollbar = System.Windows.SystemParameters.VerticalScrollBarWidth;
        double others = 0;
        for (int i = 0; i < grid.Columns.Count - 1; i++)
        {
            others += grid.Columns[i].ActualWidth;
        }

        double target = Math.Max(MinLastColumn, list.ActualWidth - others - scrollbar);
        System.Windows.Controls.GridViewColumn last = grid.Columns[^1];
        if (Math.Abs((last.ActualWidth) - target) > 1) // 防止设置宽度再次触发 SizeChanged 的抖动
        {
            last.Width = target;
        }
    }
}
