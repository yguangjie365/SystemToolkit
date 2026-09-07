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
            int thirdParty = _vm.Packages.Count(p => p.IsThirdParty);
            int selected = _vm.Packages.Count(p => p.IsSelected && p.IsThirdParty);
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
    /// 列表宽度变化时动态调整列宽（2026-09-06 响应式改造）。
    /// 策略：驱动包/类别列弹性[180,280]，提供商/版本/设备按比例分配剩余空间。
    /// </summary>
    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not ListView list || list.View is not System.Windows.Controls.GridView gv || gv.Columns.Count < 6)
        {
            return;
        }

        const double scrollbarAllowance = 24;                       // 垂直滚动条 + 边框占位
        const double checkboxCol = 36;                              // 勾选列固定
        const double dateCol = 72;                                  // 日期列固定

        double viewport = e.NewSize.Width - scrollbarAllowance;
        double nameMin = 180, nameMax = 280;
        double nameWidth = Math.Clamp(viewport - checkboxCol - dateCol, nameMin, nameMax);

        gv.Columns[1].Width = nameWidth;                            // 驱动包/类别
        gv.Columns[4].Width = dateCol;                              // 日期

        double surplus = viewport - checkboxCol - dateCol - nameWidth;
        if (surplus > 0)
        {
            gv.Columns[2].Width = 80 + surplus * 0.20;              // 提供商
            gv.Columns[3].Width = 90 + surplus * 0.15;              // 版本
            gv.Columns[5].Width = 150 + surplus * 0.35;             // 设备/状态
        }
        else
        {
            gv.Columns[2].Width = 80;                               // 最小保底
            gv.Columns[3].Width = 90;
            gv.Columns[5].Width = 150;
        }
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
