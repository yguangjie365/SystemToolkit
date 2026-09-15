using System.Windows;
using System.Windows.Controls;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 网络管理视图：6 Tab（设置/诊断/修复/优化/局域网扫描/分流路由）+ 底部共享日志面板。
/// Loaded 完成组合根接线（确认回调 + 首屏数据，幂等）；Tab 切换由 code-behind 控制面板可见性
/// （AppManager 同款机制——不改布局结构，无响应式重排）。
/// </summary>
public partial class NetManagerView : UserControl
{
    private NetManagerViewModel Vm => (NetManagerViewModel)DataContext;

    /// <summary>🟠 G-🟠-4（2026-09-15）：构造参数直存字段 —— Tab 切换不再依赖
    /// <c>DataContext</c> 强转（后者在 DataContext 被外部改写/置空时会静默跳过）。</summary>
    private readonly NetManagerViewModel _vm;

    private bool _loaded;

    /// <summary>XAML 解析是否已完成（<c>InitializeComponent</c> 返回后置位）。
    /// <para>
    /// <c>IsChecked="True"</c> 会在**解析期**就触发 <c>OnTabChecked</c> —— 此时各 <c>x:Name</c>
    /// 面板字段尚未赋值，<c>ShowPanel</c> 必须跳过（初始可见性由 XAML 的默认 <c>Visibility</c> 承担）。
    /// 与 <see cref="OnLogToggleChanged"/> 是同一个时序陷阱。
    /// </para>
    /// </summary>
    private readonly bool _initialized;

    public NetManagerView(NetManagerViewModel vm)
    {
        // 🔴 顺序不可换：`_vm` 必须在 InitializeComponent **之前**赋值（XAML 里 IsChecked="True"
        // 会在解析期触发 OnTabChecked，那时就只能靠 `_initialized` 拦住 ShowPanel）。
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        _initialized = true;
        Loaded += OnLoaded;
        // 审查 O7（2026-09-10）：切页卸载/关窗时取消持续 ping（循环与 VM 常驻泄漏）；NET-6 同款收口自动监控
        // 🟡 V14-N11：直接用构造参数 vm（= 上面刚赋给 DataContext 的同一个实例），
        // 不再每次触发都做一遍 `DataContext as NetManagerViewModel` 的强制转换
        // —— 后者在 DataContext 被外部改写/置空时会静默跳过取消（正是本回调要防的泄漏路径）。
        Unloaded += (_, _) =>
        {
            vm.Diagnostics.CancelPing();
            vm.Lan.CancelMonitor();
            vm.Split.CancelGuard();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 🟠 V16-1（2026-09-15）：先按**用户意图**恢复「因卸载而暂停」的后台循环。
        // 批② 把 View 改 Transient 后，主题切换会重建视图并触发 Unloaded（三条 Cancel），
        // 新实例的 OnLoaded 若不恢复，用户主动启动的 ping / 自动监控就被永久停掉。
        // 🔴 判据在 **VM（单例）** 的意图位上、与视图实例无关，故必须放在 `_loaded` 早退**之前**
        // —— 与 FileTransferView.ResumeTimer 同口径（那里同样必须在早退前）。
        // 注意：Split 的守护不在其列 —— 它由 LoadAsync 的「台账在位」判据恢复（见 CancelGuard 裁定注释）。
        Vm.Diagnostics.ResumePingIfIntended();
        Vm.Lan.ResumeMonitorIfIntended();

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
            Split.ConfirmRequest = Vm.ConfirmRequest;
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

    private SplitRouteTabViewModel Split => Vm.Split;

    /// <summary>Tab 切换：改选中索引 + 切面板可见性（不动布局结构）。
    /// 🟠 G-🟠-4：改用构造参数字段 <c>_vm</c>，不再做 <c>DataContext as NetManagerViewModel</c>
    /// —— 后者在 DataContext 被外部改写/置空时会**静默跳过**（点 Tab 无反应且无日志），
    /// 正是 V14-N11 在 Unloaded 回调上修掉的同一个问题。</summary>
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        // 解析期触发（XAML 里 IsChecked="True"）：面板字段未就绪 → 跳过，初始可见性由 XAML 默认值承担。
        if (!_initialized)
        {
            return;
        }

        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int index))
        {
            _vm.SelectedTabIndex = index;
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
        SplitPanel.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }
}
