using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Modules.DriverManager;

/// <summary>
/// 单行驱动包 VM：展示投影 + 勾选状态（列表勾选 → 底部状态栏计数）。
/// </summary>
public partial class DriverPackageVm : ObservableObject
{
    public DriverPackage Model { get; }

    public DriverPackageVm(DriverPackage model)
    {
        Model = model;
    }

    public string InfName => Model.PublishedName;
    public string Provider => string.IsNullOrWhiteSpace(Model.Provider) ? "—" : Model.Provider;
    public string Version => string.IsNullOrWhiteSpace(Model.Version) ? "—" : Model.Version;
    public string DateText => Model.Date?.ToString("yyyy/MM/dd") ?? "—";
    public string ClassName => string.IsNullOrWhiteSpace(Model.ClassName) ? "未分类" : Model.ClassName;
    public bool IsThirdParty => Model.IsThirdParty;
    public bool IsOldVersion => Model.State == DriverPackageState.OldVersion;

    /// <summary>系统关键（收件箱或启动关键）：pnputil 拒绝 / 删除可致蓝屏，UI 禁止勾选（FR-06 验收断言）。</summary>
    public bool CanSelect => !IsSystemCritical;
    public bool IsSystemCritical => Model.State == DriverPackageState.SystemCritical;
    public bool IsNoAssociation => Model.State == DriverPackageState.NoDeviceAssociation;

    /// <summary>勾选禁用原因：系统关键分两类——收件箱 / 第三方启动关键（存储控制器等，删除可致蓝屏或无法开机）。</summary>
    public string SelectDisabledToolTip => Model.IsThirdParty
        ? "启动关键设备类驱动（如存储控制器）——删除可致蓝屏或无法开机，已禁止删除"
        : "收件箱驱动——随 Windows 发行，pnputil 拒绝删除";

    /// <summary>签名者（v2.0 XML 源新增；长串缩写显示，完整值进 ToolTip）。</summary>
    public string SignerText
    {
        get
        {
            string s = Model.SignerName;
            if (string.IsNullOrWhiteSpace(s))
            {
                return "—";
            }

            return s.StartsWith("Microsoft Windows Hardware Compatibility", StringComparison.OrdinalIgnoreCase)
                ? "WHQL"
                : s.StartsWith("Microsoft Windows Third Party", StringComparison.OrdinalIgnoreCase)
                    ? "WHQL(第三方)"
                    : s;
        }
    }

    /// <summary>关联设备名（多设备分号连接）。</summary>
    public string DeviceNamesText => Model.DeviceNames.Count == 0 ? "—" : string.Join("; ", Model.DeviceNames);

    /// <summary>状态徽章可见性：除「当前/未知」外都展示（旧版本/系统关键/无关联）。</summary>
    public System.Windows.Visibility IsStateBadgeVisible => Model.State switch
    {
        DriverPackageState.Current => System.Windows.Visibility.Collapsed,
        DriverPackageState.Unknown => System.Windows.Visibility.Collapsed,
        _ => System.Windows.Visibility.Visible,
    };

    /// <summary>状态徽章键：0=当前 1=旧版本(琥珀) 2=系统关键(灰) 3=无关联(蓝)。</summary>
    public int StateKey => Model.State switch
    {
        DriverPackageState.OldVersion => 1,
        DriverPackageState.SystemCritical => 2,
        DriverPackageState.NoDeviceAssociation => 3,
        _ => 0,
    };

    public string StateText => Model.State switch
    {
        DriverPackageState.OldVersion => "旧版本",
        DriverPackageState.SystemCritical => "系统关键",
        DriverPackageState.NoDeviceAssociation => "无关联",
        _ => "当前",
    };

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 驱动管理页 VM（V0.3-A：扫描枚举 + 分组 + 筛选 + 搜索；V0.3-B：提权删除 + 批量导出；
/// RAPR 对照轮：备份编排下沉 DriverBackupService + 添加/安装接线 + 删除复核时序修复）。
/// 复用说明：编排逻辑为新增（旧工程无真实驱动能力，见 2026-09-04 调研结论）；
/// 枚举/操作经 pnputil 官方机制（IPnpUtilClient，DI 注入提权客户端），与 DriverStoreExplorer 同路线。
/// </summary>
public partial class DriverManagerViewModel : ObservableObject
{
    private readonly IPnpUtilClient _pnputil;
    private readonly DriverScanner _scanner;
    private readonly DriverBackupService _backup;
    private readonly ILogger _logger;

    public DriverManagerViewModel(
        IPnpUtilClient pnputil, DriverScanner scanner, DriverBackupService backup, ILogger? logger = null)
    {
        _pnputil = pnputil;
        _scanner = scanner;
        _backup = backup;
        _logger = logger ?? NullLogger.Instance;
        PackagesView = new ListCollectionView(Packages)
        {
            Filter = FilterPackage,
        };
        ((ListCollectionView)PackagesView).GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(DriverPackageVm.ClassName)));
    }

    /// <summary>确认对话框回调（View 注入；与 AppManager 同款模式，VM 不直接依赖 MessageBox）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    /// <summary>备份选项回调：View 弹出备份向导；返回 null = 用户取消。
    /// 选项 = [勾选包名]（空 = 全部第三方）+ 目标目录 + 是否"全部第三方"范围。</summary>
    public Func<(IReadOnlyList<string> Names, string DestDir, bool AllThirdParty)?>? BackupWizardRequest { get; set; }

    /// <summary>添加/安装源目录回调：View 弹目录选择器；返回 null = 用户取消。</summary>
    public Func<string?>? AddSourceFolderRequest { get; set; }

    public ObservableCollection<DriverPackageVm> Packages { get; } = new();

    /// <summary>分组视图：按驱动类别分组（Expander 可折叠，对齐 RAPR）。</summary>
    public ICollectionView PackagesView { get; }

    /// <summary>状态筛选：0=全部 1=第三方 2=Microsoft 3=旧版本 4=无关联。</summary>
    [ObservableProperty]
    private int _statusFilter;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))] // 审查 M1：8.4.2 无 CommandManager 兜底，必须显式通知
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    private bool _isScanning;

    private CancellationTokenSource? _scanCts;

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>
    /// 右侧抽屉的当前包（ListView.SelectedItem 双向绑定；M-UI-3 落地 2026-09-05）。
    /// null = 未选中 → 抽屉显示全局操作（刷新 / 添加 / 备份向导）；
    /// 非 null = 抽屉滑出该包详情 + 包级操作。
    /// 🔴 与 IsSelected（勾选，决定批量操作集）是两套独立状态：
    /// 点行 = 选中看详情；勾 CheckBox = 加入删除/备份操作集。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDrawerDetailVisible))]
    private DriverPackageVm? _selectedPackage;

    /// <summary>抽屉是否处于「包详情」态（SelectedPackage 非空）。</summary>
    public bool IsDrawerDetailVisible => SelectedPackage is not null;

    /// <summary>底部状态栏文案（找到 N 个驱动包 / 扫描失败原因）。</summary>
    [ObservableProperty]
    private string _statusText = "尚未扫描——点击「扫描(R)」枚举 Driver Store";

    partial void OnStatusFilterChanged(int value) => PackagesView.Refresh();
    partial void OnSearchQueryChanged(string value) => PackagesView.Refresh();

    private bool FilterPackage(object item)
    {
        if (item is not DriverPackageVm vm)
        {
            return false;
        }

        bool pass = StatusFilter switch
        {
            1 => vm.IsThirdParty,
            2 => !vm.IsThirdParty,
            3 => vm.IsOldVersion,
            4 => vm.IsNoAssociation,
            _ => true,
        };
        if (!pass)
        {
            return false;
        }

        string query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return vm.InfName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || vm.Provider.Contains(query, StringComparison.OrdinalIgnoreCase)
            || vm.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || vm.Version.Contains(query, StringComparison.OrdinalIgnoreCase)
            || vm.DeviceNamesText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>扫描 Driver Store：枚举 → 分类 → 补齐大小/安装日期/设备关联 → 重灌集合。</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        using var scanCts = new CancellationTokenSource();
        _scanCts = scanCts;
        try
        {
            StatusText = "正在枚举 Driver Store…";
            Progress<string> progress = new(t => StatusText = t);
            IReadOnlyList<DriverPackage> list = await _scanner.ScanAsync(scanCts.Token, progress).ConfigureAwait(true);
            DriverStoreClassifier.Classify(list, applyCleanupCategories: true);

            Packages.Clear();
            foreach (DriverPackage pkg in list)
            {
                var vm = new DriverPackageVm(pkg);
                HookSelectionCounter(vm);
                Packages.Add(vm);
            }

            PackagesView.Refresh();
            int thirdParty = list.Count(p => p.IsThirdParty);
            int old = list.Count(p => p.State == DriverPackageState.OldVersion);
            int critical = list.Count(p => p.State == DriverPackageState.SystemCritical);
            StatusText = $"找到 {list.Count} 个驱动包（第三方 {thirdParty} · 旧版本 {old} · 系统关键 {critical}）";
            _logger.Info($"驱动扫描完成：{list.Count} 个驱动包，第三方 {thirdParty}，旧版本 {old}，系统关键 {critical}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
            _logger.Info("驱动扫描已取消");
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
            _logger.Error("驱动扫描失败", ex);
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private bool CanScan => !IsScanning;

    /// <summary>取消正在进行的扫描（审查：长操作取消，命中 CancellationToken 抛 OperationCanceledException）。</summary>
    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan() => _scanCts?.Cancel();

    private bool CanCancelScan => IsScanning;

    /// <summary>是否已有提权操作进行中（删除/导出互斥；扫描可并行，互不影响）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))] // 审查 M1：四个操作命令此前永不刷新可用性
    [NotifyCanExecuteChangedFor(nameof(ForceDeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddDriversCommand))]
    private bool _isOperating;

    private bool CanOperate => !IsOperating;

    /// <summary>筛选 chips 统一入口（审查 L6：5 个 OnFilterXxx 处理器收敛为单命令；
    /// ButtonBase 仅在点击时执行命令，XAML 解析期 IsChecked 触发 Checked 的旧坑随之消除）。</summary>
    [RelayCommand]
    private void SetFilter(string? filter)
    {
        StatusFilter = int.TryParse(filter, out int value) ? value : 0;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (DriverPackageVm vm in Packages)
        {
            vm.IsSelected = false;
        }
    }

    private void AddLog(string message)
    {
        // 页面暂无日志面板：状态栏 + 日志文件双通道（与全项目"禁止静默"纪律一致）
        StatusText = message;
        _logger.Info(message);
    }

    /// <summary>行勾选变化 → 重算底栏计数（与 AppManager 同款模式）。</summary>
    private void HookSelectionCounter(DriverPackageVm vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DriverPackageVm.IsSelected))
            {
                SelectedCount = Packages.Count(p => p.IsSelected);
            }
        };
    }
}
