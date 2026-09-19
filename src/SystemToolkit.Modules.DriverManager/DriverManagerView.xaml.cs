using System.IO;
using System.Windows;
using System.Windows.Controls;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Modules.DriverManager;

/// <summary>
/// 驱动管理页交互层：筛选 chips、首次进入自动扫描。
/// 🔴 XAML 解析期 IsChecked="True" 会触发 Checked——直接跳过（AppManager 同款守卫）。
/// </summary>
public partial class DriverManagerView : UserControl
{
    private readonly DriverManagerViewModel _vm;
    private readonly ILogger _logger;
    private bool _loaded;

    public DriverManagerView(DriverManagerViewModel vm, ILogger? logger = null)
    {
        InitializeComponent();
        _vm = vm;
        _logger = logger ?? NullLogger.Instance;
        DataContext = vm;
        // 确认对话框经回调注入（AppManager 同款模式，VM 不直接依赖 MessageBox）
        _vm.ConfirmRequest = (title, message)
            => MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
               == MessageBoxResult.OK;
        // 备份向导：View 弹窗收集范围/目录，包名按范围在此处圈定
        _vm.BackupWizardRequest = () =>
        {
            // 🟠-12（UI-v2）：计数改由 VM 暴露（与 RunBackupAsync 的前置判定同一判据，避免两处各写）。
            int thirdParty = _vm.ThirdPartyPackageCount;
            int selected = _vm.SelectedThirdPartyCount;
            (IReadOnlyList<string> Names, string DestDir, bool AllThirdParty)? result =
                DriverBackupWindow.Show(Window.GetWindow(this), thirdParty, selected,
                (all, destDir) =>
                {
                    Directory.CreateDirectory(destDir);
                    return all
                        ? (IReadOnlyList<string>)_vm.Packages.Where(p => p.IsThirdParty).Select(p => p.InfName).ToList()
                        : _vm.Packages.Where(p => p.IsSelected && p.IsThirdParty).Select(p => p.InfName).ToList();
                });
            return result;
        };
        // 添加/安装源目录选择（OpenFolderDialog 为 WPF .NET 8+ 原生，无需 Win32 互操作）
        _vm.AddSourceFolderRequest = () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择包含驱动 INF 的文件夹（含子目录递归查找 .inf）",
            };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
        };
        Loaded += OnViewLoaded;
    }

    /// <summary>
    /// 列表宽度变化时动态调整列宽（2026-09-06 响应式改造；2026-09-15 实机反馈后改为方案 B）。
    /// <para>策略：**先为各列预留最小值**（勾选 36 / 日期 72 / 提供商 80 / 版本 90 / 设备 150），
    /// 剩下的才给「驱动包/类别」列（弹性区间 [150,280]），若还有余量再按 0.20 / 0.15 / 0.65
    /// 分给提供商 / 版本 / 设备。⇒ **六列总宽恒等于 viewport**，末列不再被裁。</para>
    /// <para>🟡 方案 B（用户选定）：窄窗下允许「驱动包/类别」收窄到 150（原下限 180），
    /// 把空间让给设备列 —— 原实现在窄窗把末列挤到 100px，用户看不到设备名。</para>
    /// </summary>
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListView list || list.View is not System.Windows.Controls.GridView gv || gv.Columns.Count < 6)
        {
            return;
        }

        const double scrollbarAllowance = 24;                       // 垂直滚动条 + 边框占位
        const double dateCol = 72;                                  // 日期列固定

        double viewport = e.NewSize.Width - scrollbarAllowance;

        // 🔴 B-🟡-2（2026-09-15 实机反馈后修复，方案 B）：原先 `surplus` **一分钱最小值都没扣**
        //   （`viewport - checkboxCol - dateCol - nameWidth`），而 nameWidth 又按「整个剩余宽度」
        //   取到上限 280 ⇒ 六列总宽恒 = viewport + 320（80+90+150 被重复计入）。叠加 XAML 的
        //   `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` ⇒ 末列右侧被裁且**无法横向滚动**，
        //   实机症状是窄窗下「设备/状态」列只剩一个徽章、设备名整片看不见。
        //   正解（落在 ComputeColumnWidths）：**先预留其余列的最小值**，剩下的才给名称列。
        (double nameWidth, double providerWidth, double versionWidth, double deviceWidth) =
            ComputeColumnWidths(viewport);

        gv.Columns[1].Width = nameWidth;                            // 驱动包/类别
        gv.Columns[2].Width = providerWidth;                        // 提供商
        gv.Columns[3].Width = versionWidth;                         // 版本
        gv.Columns[4].Width = dateCol;                              // 日期
        gv.Columns[5].Width = deviceWidth;                          // 设备/状态（吃下全部余量）

        // ⚠️ 本方法写 Columns[5] 与 `GridViewColumnSizing.AutoFillLastColumn`（XAML :87）
        //   算的是**同一个量**（viewport 余量）—— 扣完预留后两者趋于一致，不再互相覆盖。
        //   原先两者差 320px（一个按「余量」、一个按「整个剩余」），谁后执行谁说了算，
        //   实机表现就是末列忽宽忽窄。
    }

    /// <summary>
    /// 列宽计算（**纯函数，便于单测**；2026-09-15 抽出自 <see cref="OnListSizeChanged"/>）。
    /// <para>不变量：只要 viewport 足够，`36 + Name + Provider + Version + 72 + Device == viewport`
    /// —— 六列总宽恰好铺满，**不会溢出**（溢出会被裁且横向滚动被禁用）。</para>
    /// <para>方案 B：窄窗时名称列先收窄（下限 150），把空间让给设备列。</para>
    /// </summary>
    /// <param name="viewport">列表可用宽度（已扣滚动条占位）。</param>
    internal static (double Name, double Provider, double Version, double Device) ComputeColumnWidths(double viewport)
    {
        const double checkboxCol = 36;
        const double dateCol = 72;
        const double providerMin = 80;
        const double versionMin = 90;
        const double deviceMin = 150;
        const double reserved = checkboxCol + dateCol + providerMin + versionMin + deviceMin;

        const double nameMin = 150;
        const double nameMax = 280;
        double nameWidth = Math.Clamp(viewport - reserved, nameMin, nameMax);

        double surplus = viewport - reserved - nameWidth;
        return surplus > 0
            ? (nameWidth,
               providerMin + surplus * 0.20,
               versionMin + surplus * 0.15,
               deviceMin + surplus * 0.65)
            : (nameWidth, providerMin, versionMin, deviceMin);
    }

    /// <summary>首次进入页面：自动扫描 Driver Store（枚举只读，无需提权）。</summary>
    private async void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        // 🔴 async void 内的异常无法被调用方捕获，会被抛回 Dispatcher 的 SynchronizationContext，
        //    未处理即进程终止（0xE0434352）。此处必须兜底：扫描失败只降级为状态栏提示，
        //    绝不允许把整个应用带崩。
        try
        {
            // 命名约定：ScanAsync → ScanCommand（CommunityToolkit 对 async 方法剥 Async 后缀）
            await _vm.ScanCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.Error("驱动管理页首次扫描失败", ex);
            _vm.StatusText = $"扫描失败：{ex.Message}";
        }
    }





}
