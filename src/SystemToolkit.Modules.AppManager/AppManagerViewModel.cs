using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.AppManager;

/// <summary>
/// 软件管理页 VM。
/// <para>复用来源：旧工程 EnvManagerViewModel（1221 行）的编排逻辑——winget 写操作串行闸、
/// 检测循环（msstore 短 Id 并发补查）、安装/升级/卸载局部状态更新、导入导出含自动备份、
/// 换源三件套、退出码语义翻译、提前 return 留痕纪律。改造点：三分区→微软商店/第三方两清单
/// （按 IsMsStore 拆分，驱动数据内部保留仅供导出回路）；IDialogService/ConfigService 改直调
/// 与回调注入；新增批量勾选与环境档案（按分类聚合，MVP）。</para>
/// </summary>
public partial class AppManagerViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly EnvListService _env;
    private readonly IWingetClient _winget;
    private readonly ILogger _logger;

    /// <summary>winget 写操作串行闸（winget 有进程级互斥锁，并发调用互相报错）。</summary>
    private readonly SemaphoreSlim _wingetGate = new(1, 1);

    private bool _initialized;

    /// <summary>进行中批量安装的取消令牌（BatchInstallAsync 赋值，结束置空；审查 2026-09-04 P2）。</summary>
    private CancellationTokenSource? _batchCts;

    public AppManagerViewModel(EnvListService env, IWingetClient winget, ILogger? logger = null)
    {
        _env = env;
        _winget = winget;
        _logger = logger ?? NullLogger.Instance;
        // winget 输出挂接日志面板；过滤进度刷新行（\r 回车 / \b 退格）
        _winget.OutputSink = line =>
        {
            string clean = line.TrimEnd('\r');
            if (clean.Contains('\b') || clean.Contains('\r'))
            {
                return;
            }

            AddLog("[winget] " + clean);
        };
    }

    // ==================================================================
    // 集合
    // ==================================================================
    /// <summary>微软商店应用（Source=msstore）。</summary>
    public ObservableCollection<WingetPackageVm> StorePackages { get; } = new();

    /// <summary>第三方应用（Source=winget 或手动维护）。</summary>
    public ObservableCollection<WingetPackageVm> ThirdPartyPackages { get; } = new();

    /// <summary>驱动清单（内部保留：导入导出回路不能丢数据；驱动管理属 DriverManager 模块）。</summary>
    private readonly List<ManualSoftware> _driverSoftwares = new();

    public ObservableCollection<ManualSoftware> ManualSoftwares { get; } = new();

    public ObservableCollection<WingetSearchResult> SearchResults { get; } = new();

    public ObservableCollection<LogLine> LogLines { get; } = new();

    /// <summary>环境档案卡片（按分类聚合的 MVP 实现）。</summary>
    public ObservableCollection<EnvironmentArchiveVm> Archives { get; } = new();

    /// <summary>确认对话框回调（View 注入，避免 VM 依赖 MessageBox）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    /// <summary>软件编辑对话框回调（View 注入；null=新增第三方手动条目）。用户 2026-09-04：列表编辑功能必须有。</summary>
    public Func<object?, SoftwareEditResult?>? SoftwareEditRequest { get; set; }

    private bool CanOperate => !IsOperating && !IsRefreshing; // 审查 2026-09-04：刷新中发起写操作会撞 winget 进程互斥锁

    // ==================================================================
    // 可观察属性
    // ==================================================================
    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isSearchPopupOpen;

    [ObservableProperty]
    private ICollectionView? _storeView;

    [ObservableProperty]
    private ICollectionView? _thirdPartyView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))] // 审查 2026-09-04：刷新态变化需联动写操作按钮
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwitchToMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreOfficialSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(BatchInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallSearchResultCommand))] // 审查 M1：漏出通知列表的 2 命令补齐
    [NotifyCanExecuteChangedFor(nameof(RestoreEnvironmentCommand))]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _refreshStatus = "点击「检测状态」查询安装状态";

    /// <summary>状态筛选：0=全部 1=已安装 2=有更新 3=未安装。</summary>
    [ObservableProperty]
    private int _statusFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatchBarVisible))] // M-UI-2：浮底批量条可见性
    private int _selectedCount;

    /// <summary>浮底批量操作条可见性（SelectedCount>0 时显示；M-UI-2 落地 2026-09-05）。</summary>
    public bool IsBatchBarVisible => SelectedCount > 0;

    /// <summary>当前软件源显示（官方 / 中科大），随换源操作更新。</summary>
    [ObservableProperty]
    private string _sourceLabel = "官方";

    /// <summary>是否有 winget 写操作进行中；为 true 时写操作按钮整体禁用。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwitchToMirrorCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreOfficialSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(BatchInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallSearchResultCommand))] // 审查 M1：漏出通知列表的 2 命令补齐
    [NotifyCanExecuteChangedFor(nameof(RestoreEnvironmentCommand))]
    private bool _isOperating;

    // ==================================================================
    // 加载
    // ==================================================================

    /// <summary>行勾选变化 → 重算「已选 N 项」计数（Load 与编辑替换共用；审查 2026-09-04 P1-2）。</summary>
    private void HookSelectionCounter(WingetPackageVm vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WingetPackageVm.IsSelected))
            {
                SelectedCount = StorePackages.Count(p => p.IsSelected) + ThirdPartyPackages.Count(p => p.IsSelected);
            }
        };
    }

    public Task LoadAsync()
    {
        if (_initialized)
        {
            return Task.CompletedTask;
        }

        // 审查 2026-09-04（P1-4）：Load 抛异常（配置目录不可写等）不能把本页打成永久空白
        EnvCatalog catalog;
        try
        {
            catalog = _env.Load();
        }
        catch (Exception ex)
        {
            _logger.Error("软件清单加载失败，按空清单继续", ex);
            AddLog("⚠ 软件清单加载失败（详见日志），已按空清单继续");
            catalog = new EnvCatalog();
        }

        _initialized = true; // 仅在加载（或降级）成功后置位

        StorePackages.Clear();
        ThirdPartyPackages.Clear();
        foreach (WingetPackage item in catalog.Winget)
        {
            var vm = new WingetPackageVm(item);
            HookSelectionCounter(vm);
            if (item.IsMsStore)
            {
                StorePackages.Add(vm);
            }
            else
            {
                ThirdPartyPackages.Add(vm);
            }
        }

        ManualSoftwares.Clear();
        foreach (ManualSoftware item in catalog.Manual)
        {
            ManualSoftwares.Add(item);
        }

        _driverSoftwares.Clear();
        _driverSoftwares.AddRange(catalog.Driver);

        RebuildViews();
        RebuildArchives();
        AddLog($"已加载软件清单：{StorePackages.Count} 个商店应用，{ManualSoftwares.Count + ThirdPartyPackages.Count} 个第三方应用");
        return Task.CompletedTask;
    }

    private void RebuildViews()
    {
        StoreView = CollectionViewSource.GetDefaultView(StorePackages);
        StoreView.Filter = MatchesFilter;
        ThirdPartyView = CollectionViewSource.GetDefaultView(ThirdPartyPackages);
        ThirdPartyView.Filter = MatchesFilter;
    }

    /// <summary>状态筛选 + 本地搜索（名称/Id）。异步检测与本地搜索互不干扰。</summary>
    private bool MatchesFilter(object item)
    {
        if (item is not WingetPackageVm vm)
        {
            return false;
        }

        return StatusFilter switch
        {
            1 => vm.State == WingetPackageState.Installed,
            2 => vm.State == WingetPackageState.Updatable,
            3 => vm.State == WingetPackageState.NotInstalled,
            _ => true,
        };
    }

    partial void OnStatusFilterChanged(int value)
    {
        StoreView?.Refresh();
        ThirdPartyView?.Refresh();
    }

    /// <summary>当前激活 Tab：0=微软商店 1=第三方 2=环境档案（View 切 Tab 时回写）。</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    [RelayCommand]
    private void SelectAll()
    {
        // 只作用于当前 Tab（用户 2026-09-04：跨 Tab 误选）
        foreach (WingetPackageVm vm in ActivePackages().Where(IsVisibleIn))
        {
            vm.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        // 与 SelectAll 同语义：仅当前 Tab 且可见行（审查 M4——跨 Tab/被筛选隐藏行不应被波及）
        foreach (WingetPackageVm vm in ActivePackages().Where(IsVisibleIn))
        {
            vm.IsSelected = false;
        }
    }

    /// <summary>反选（审查 2026-09-04 P2：原"反选"按钮误绑 ClearSelectionCommand，文案与行为不符）。</summary>
    [RelayCommand]
    private void InvertSelection()
    {
        // 与 SelectAll 同语义：仅当前 Tab 且可见行（审查 M4——隐藏行被反选后会被批量安装）
        foreach (WingetPackageVm vm in ActivePackages().Where(IsVisibleIn))
        {
            vm.IsSelected = !vm.IsSelected;
        }
    }

    /// <summary>筛选 chips 统一入口（审查 L6：4 个 OnFilterXxx 处理器收敛为单命令）。</summary>
    [RelayCommand]
    private void SetFilter(string? filter)
    {
        StatusFilter = int.TryParse(filter, out int value) ? value : 0;
    }

    private IEnumerable<WingetPackageVm> ActivePackages()
        => SelectedTabIndex == 0 ? StorePackages : ThirdPartyPackages;

    private bool IsVisibleIn(WingetPackageVm vm)
    {
        ICollectionView? view = SelectedTabIndex == 0 ? StoreView : ThirdPartyView;
        return view?.Contains(vm) != false;
    }

    // ==================================================================
    // 持久化
    // ==================================================================
    private void PersistAll()
    {
        try
        {
            _env.SaveWinget(StorePackages.Concat(ThirdPartyPackages).Select(v => v.Model));
            _env.SaveManual(ManualSoftwares);
            _env.SaveDriver(_driverSoftwares);
            AddLog("软件清单已保存");
        }
        catch (Exception ex)
        {
            _logger.Error("保存软件清单失败", ex);
            AddLog("保存软件清单失败：" + ex.Message);
        }
    }

    // ==================================================================
    // 日志
    // ==================================================================
    public void AddLog(string message) => LogFeed.Append(LogLines, message, MaxLogLines);

    [RelayCommand]
    private void ClearLog() => LogFeed.Clear(LogLines);

}
