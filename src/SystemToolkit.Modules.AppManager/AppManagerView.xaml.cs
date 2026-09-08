using System.Windows;
using System.Windows.Controls;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Modules.AppManager;

/// <summary>
/// 软件管理页交互层：Tab 切换、软件源下拉、确认回调注入、首启异步加载。
/// </summary>
public partial class AppManagerView : UserControl
{
    private readonly AppManagerViewModel _vm;
    private readonly ILogger _logger;
    private bool _loaded;

    public AppManagerView(AppManagerViewModel vm, ILogger? logger = null)
    {
        InitializeComponent();
        _vm = vm;
        _logger = logger ?? NullLogger.Instance;
        DataContext = vm;
        // 确认对话框经回调注入（VM 不直接依赖 MessageBox，与旧工程同款模式）
        vm.ConfirmRequest = (title, message)
            => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
               == MessageBoxResult.OK;
        // 信息提示回调（审查 O4：NewArchive 的提示不再直调 MessageBox）
        vm.InfoRequest = (message, title)
            => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        // 导入/导出路径回调（审查 O6：对话框一律 View 注入）
        vm.PickSavePath = () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出软件清单",
                Filter = "JSON 清单 (*.json)|*.json",
                FileName = $"systemtoolkit_list_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        };
        vm.PickOpenPath = () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入软件清单",
                Filter = "JSON 清单 (*.json)|*.json",
                CheckFileExists = true,
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        };
        // 列表编辑对话框（用户 2026-09-04：编辑功能必须有）
        vm.SoftwareEditRequest = item => SoftwareEditWindow.Show(Window.GetWindow(this), item);
        Loaded += OnViewLoaded;
    }

    /// <summary>首次进入页面：加载清单（快），随后后台异步检测安装状态（不阻塞 UI）。</summary>
    private async void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        // 审查 2026-09-04（P2）：兑现批量安装"关闭窗口即停"的承诺
        Window.GetWindow(this)?.Closed += (_, _) => _vm.CancelBatchInstall();
        try
        {
            await _vm.LoadAsync();
            _ = _vm.RefreshStatesAsync();
        }
        catch (Exception ex)
        {
            // 审查 O5：async void 无人接异常——降级提示而非打崩进程
            _logger.Error("软件管理页加载失败", ex);
            _vm.AddLog("⚠ 页面加载失败：" + ex.Message);
        } // 用户确认：异步检测，不阻塞
    }

    /// <summary>Tab 切换：三视图互斥（环境档案内容绝不出现在软件 Tab）。</summary>
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        // 🔴 XAML 解析期 IsChecked="True" 会触发 Checked，此时 _vm/元素尚未就绪——直接跳过
        //（XAML 里的初始勾选状态即想要的初始状态，跳过无副作用）
        if (sender is not RadioButton { Tag: string tag }
            || _vm is null
            || StoreList is null || ThirdPartyListPanel is null
            || ArchivesPanel is null || FilterRow is null)
        {
            return;
        }

        bool showStore = tag == "store";
        bool showThird = tag == "thirdparty";
        bool showArchives = tag == "archives";

        _vm.SelectedTabIndex = showStore ? 0 : showThird ? 1 : 2;
        StoreList.Visibility = showStore ? Visibility.Visible : Visibility.Collapsed;
        ThirdPartyListPanel.Visibility = showThird ? Visibility.Visible : Visibility.Collapsed;
        FilterRow.Visibility = showArchives ? Visibility.Collapsed : Visibility.Visible;
        ArchivesPanel.Visibility = showArchives ? Visibility.Visible : Visibility.Collapsed;
        // 源搜索框仅微软商店 Tab 显示（用户 2026-09-04）
        SearchHost.Visibility = showStore ? Visibility.Visible : Visibility.Collapsed;
    }





    private void OnSourceMenuClick(object sender, RoutedEventArgs e)
        => SourcePopup.IsOpen = !SourcePopup.IsOpen;

    /// <summary>
    /// 列宽自适应：操作列固定 330（四按钮：安装/升级/卸载/编辑，用户 2026-09-07 恢复），
    /// 名称列 = 剩余空间（下限 280 / 上限 330），
    /// 再有富余按比例分给版本/来源/状态。注意要预留垂直滚动条宽度，
    /// 且窗口 MinWidth 已保证最窄时不溢出（用户 2026-09-04）。
    /// </summary>
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListView list)
        {
            return;
        }

        // 名称列 = 第 2 列（索引 1）；GridViewColumn 非 FrameworkElement，不能 x:Name，按索引访问
        if (list.View is not System.Windows.Controls.GridView gv || gv.Columns.Count < 6)
        {
            return;
        }

        const double scrollbarAllowance = 24;                       // 垂直滚动条 + 边框占位
        const double fixedCols = 36;                                // 勾选列固定
        const double actionsWidth = 330;                            // 操作列 330（恢复固定四按钮：安装/升级/卸载/编辑）
        const double nameMin = 280, nameMax = 330;                  // 名称列 [280, 330]

        double viewport = e.NewSize.Width - scrollbarAllowance;
        double nameWidth = Math.Clamp(viewport - fixedCols - actionsWidth, nameMin, nameMax);
        gv.Columns[1].Width = nameWidth;
        gv.Columns[5].Width = actionsWidth;

        double surplus = viewport - fixedCols - actionsWidth - nameWidth;
        if (surplus > 0)
        {
            gv.Columns[2].Width = surplus * 0.25; // 版本（按比例）
            gv.Columns[3].Width = surplus * 0.20; // 来源（按比例）
            gv.Columns[4].Width = surplus * 0.30; // 状态（按比例）
        }
        else
        {
            gv.Columns[2].Width = 100; // 最小保底
            gv.Columns[3].Width = 80;
            gv.Columns[4].Width = 120;
        }
    }

    private void OnSourceMenuItemClick(object sender, RoutedEventArgs e)
        => SourcePopup.IsOpen = false;
}
