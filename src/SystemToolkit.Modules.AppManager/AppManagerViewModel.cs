using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;
using SystemToolkit.UI.Common;
using SystemToolkit.Core.Utilities;

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
    private readonly PackageIgnoreStore _ignoreStore;

    /// <summary>忽略清单（包级"永久忽略"）。🔴 判据只有 <see cref="PackageIgnoreList.IsIgnored"/> 一份。</summary>
    private PackageIgnoreList _ignoreList = new PackageIgnoreList();

    private readonly InstallHistoryStore _historyStore;

    /// <summary>安装历史（最新在前）。🔴 独立存储：`AppLog` 零读取 API 且有保留期 + 10MB 滚动，不能当历史源。</summary>
    private InstallHistoryLog _history = new InstallHistoryLog();

    /// <summary>winget 写操作串行闸（winget 有进程级互斥锁，并发调用互相报错）。</summary>
    private readonly SemaphoreSlim _wingetGate = new(1, 1);

    private bool _initialized;

    /// <summary>进行中批处理的取消令牌（<c>BatchInstallAsync</c> 与 <c>RestoreEnvironmentAsync</c> 均会赋值，
    /// 结束置空；由 <c>CancelOperation</c>（界面按钮）与 <c>CancelBatchInstall</c>（关窗钩子）两个入口取消）。
    /// 审查 2026-09-04 P2；v11~v14 后续批次补注——原注释只提批量安装，与实现（Archives 侧也赋值）不符。</summary>
    private CancellationTokenSource? _batchCts;

    public AppManagerViewModel(
        EnvListService env,
        IWingetClient winget,
        ILogger? logger = null,
        PackageIgnoreStore? ignoreStore = null,
        InstallHistoryStore? historyStore = null)
    {
        _env = env;
        _winget = winget;
        _logger = logger ?? NullLogger.Instance;
        _ignoreStore = ignoreStore ?? new PackageIgnoreStore(log: AddLog);
        _historyStore = historyStore ?? new InstallHistoryStore(log: AddLog);
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

    /// <summary>安装历史行（最新在前；供环境档案页内的「安装历史」分区展示）。</summary>
    public ObservableCollection<InstallHistoryEntry> InstallHistory { get; } = new();

    /// <summary>环境档案卡片（按分类聚合的 MVP 实现）。</summary>
    public ObservableCollection<EnvironmentArchiveVm> Archives { get; } = new();

    /// <summary>确认对话框回调（View 注入，避免 VM 依赖 MessageBox）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    /// <summary>软件编辑对话框回调（View 注入；null=新增第三方手动条目）。用户 2026-09-04：列表编辑功能必须有。</summary>
    public Func<object?, SoftwareEditResult?>? SoftwareEditRequest { get; set; }

    /// <summary>信息提示回调（View 注入；审查 O4：VM 不直接依赖 MessageBox）。</summary>
    public Action<string, string>? InfoRequest { get; set; }

    /// <summary>导出保存路径回调（View 注入；审查 O6：对话框一律注入）。</summary>
    public Func<string?>? PickSavePath { get; set; }

    /// <summary>导入打开路径回调（View 注入；审查 O6）。</summary>
    public Func<string?>? PickOpenPath { get; set; }

    /// <summary>导出报告（CSV）时的路径选择回调（View 注入；与清单导出的对话框文案分开）。</summary>
    public Func<string?>? PickReportPath { get; set; }

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
    [NotifyCanExecuteChangedFor(nameof(ExportInstalledCommand))] // v15 核实：补唯一真漏项（该命令未挂 UI 入口，属一致性加固；两字段须成对补）
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
    [NotifyCanExecuteChangedFor(nameof(ExportInstalledCommand))] // v15 核实：同上（与 _isRefreshing 成对；漏一处会让"刷新态"或"忙态"单边失灵）
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
                RecountSelection();
            }
        };
    }

    /// <summary>重算勾选数（审查 O1：删除/替换行不触发 IsSelected 变化，必须显式回算）。</summary>
    private void RecountSelection() =>
        SelectedCount = StorePackages.Count(p => p.IsSelected) + ThirdPartyPackages.Count(p => p.IsSelected);

    public async Task LoadAsync()
    {
        if (_initialized)
        {
            return;
        }

        // 审查 2026-09-04（P1-4）：Load 抛异常（配置目录不可写等）不能把本页打成永久空白
        // 审查 O16（2026-09-10）：_env.Load() 读盘移出 UI 线程
        EnvCatalog catalog;
        try
        {
            catalog = await Task.Run(() => _env.Load()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("软件清单加载失败，按空清单继续", ex);
            AddLog("⚠ 软件清单加载失败（详见日志），已按空清单继续");
            catalog = new EnvCatalog();
        }

        // 忽略清单（与清单同目录）：读盘同样移出 UI 线程；失败按空清单继续（不影响主清单）
        try
        {
            _ignoreList = await Task.Run(() => _ignoreStore.Load()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("忽略清单加载失败，按空清单继续", ex);
            _ignoreList = new PackageIgnoreList();
        }

        // 安装历史（独立存储）：同样移出 UI 线程；失败按空历史继续
        try
        {
            _history = await Task.Run(() => _historyStore.Load()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("安装历史加载失败，按空历史继续", ex);
            _history = new InstallHistoryLog();
        }

        RefreshInstallHistory();

        StorePackages.Clear();
        ThirdPartyPackages.Clear();
        foreach (WingetPackage item in catalog.Winget)
        {
            WingetPackageVm vm = CreateRow(item);
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

        // 🟡 A-🟡-5（两批审查）：置位移到**方法末尾**——原先在首次读盘之后就置位，其后任一语句
        // 抛异常都会让“已初始化”成立而清单停在半成品；且重进页面时 OnViewLoaded 的 _loaded
        // 也已为 true ⇒ 再也修不好（只能重启应用）。放在末尾后，失败可随下次进入重试。
        _initialized = true;
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
            4 => vm.IsIgnored,
            _ => true,
        };
    }

    partial void OnStatusFilterChanged(int value)
    {
        StoreView?.Refresh();
        ThirdPartyView?.Refresh();
    }

    // ==================================================================
    // 忽略清单（包级「永久忽略」）—— 落地计划 B4-①③
    // 🔴 判据只有 PackageIgnoreList.IsIgnored 一份；此处固定传 null 候选版本，
    //    即**只有"永久忽略"条目会命中**（"跳过此版本"的 UI 留后续批次，Core 已支持）。
    // ==================================================================

    /// <summary>挂接行内忽略命令（ContextMenu 回不到页 VM → 命令必须在项 VM 上）。</summary>
    private void HookIgnore(WingetPackageVm vm) => vm.HookIgnore(IgnoreSingle, UnignoreSingle);

    /// <summary>
    /// 🔴 **建行工厂**（V11-A1）—— 新建 <see cref="WingetPackageVm"/> 行的**唯一**入口。
    /// <para>
    /// 三件套缺一不可：① 勾选计数订阅（<see cref="HookSelectionCounter"/>）；
    /// ② 忽略命令挂接（<see cref="HookIgnore"/>）—— 漏挂则右键「永久忽略」**可点但静默无操作**
    /// （<c>IgnoreCommand</c> 走空条件调用 <c>_ignoreRequest?.Invoke(this)</c>：无日志、无写入）；
    /// ③ 忽略态回写（<see cref="WingetPackageVm.IsIgnored"/>）—— 漏写则编辑一个**已忽略**的软件后，
    /// 该行从「已忽略」视图消失（而忽略清单里其实还在），用户视角＝"我没取消忽略，它却不见了"。
    /// </para>
    /// <para>
    /// 四处建行（<c>LoadAsync</c> / <c>EditSoftware</c> / <c>ImportList</c> /
    /// <c>InstallSearchResultAsync</c>）此前各写各的，除 <c>LoadAsync</c> 外全漏了 ②③；
    /// 且全仓**无任何测试构造 <c>WingetPackageVm</c>**，故既有 1600+ 条测试一条都抓不到。
    /// 今后**任何**建行都必须走本方法（回归锁见 <c>AppManagerIgnoreRowTests</c>）。
    /// </para>
    /// </summary>
    private WingetPackageVm CreateRow(WingetPackage package)
    {
        var vm = new WingetPackageVm(package);
        HookSelectionCounter(vm);
        HookIgnore(vm);
        // 候选版本传 null：本轮 UI 只提供"永久忽略"，故只有永久条目会命中
        vm.IsIgnored = _ignoreList.IsIgnored(vm.Id, vm.Model.Source, null);
        return vm;
    }

    /// <summary>写入/移除一条忽略记录并（可选）落盘。</summary>
    private bool SetIgnore(WingetPackageVm vm, bool ignored, bool persist = true)
    {
        if (ignored)
        {
            _ignoreList.AddOrUpdate(new PackageIgnoreEntry
            {
                Id = vm.Id,
                Source = vm.Model.Source,
                Scope = PackageIgnoreScope.Permanent,
                RecordedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            _ignoreList.Remove(vm.Id);
        }

        vm.IsIgnored = ignored;
        return !persist || PersistIgnoreList();
    }

    /// <summary>落盘忽略清单。失败只留痕并回传 false（本次修改仍在内存生效，但不持久）。</summary>
    private bool PersistIgnoreList()
    {
        try
        {
            _ignoreStore.Save(_ignoreList);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("保存忽略清单失败", ex);
            AddLog($"⚠ 忽略清单保存失败：{ex.Message}（本次修改仅本次运行有效）");
            return false;
        }
    }

    /// <summary>重刷两个列表的筛选（忽略状态变化后行会进出"已忽略"视图）。</summary>
    private void RefreshViews()
    {
        StoreView?.Refresh();
        ThirdPartyView?.Refresh();
    }

    /// <summary>右键菜单：忽略单个软件。</summary>
    private void IgnoreSingle(WingetPackageVm vm)
    {
        // 🟡 v18-🟡-1（2026-09-16）：消费 SetIgnore 返回值区分文案——与 IgnoreSelected 对齐。
        // 原先丢弃返回值，落盘失败时仍显示"已忽略：X"，用户易读成"已持久化"（实际仅本次运行有效）。
        bool saved = SetIgnore(vm, ignored: true);
        RefreshViews();
        AddLog(saved
            ? $"已忽略：{vm.Name}（不再提示其更新；批量安装/恢复环境时会跳过）"
            : $"已忽略：{vm.Name}（⚠ 未持久化，重启后失效）");
    }

    /// <summary>右键菜单：取消忽略。</summary>
    private void UnignoreSingle(WingetPackageVm vm)
    {
        // 🟡 v18-🟡-1：同 IgnoreSingle，消费 SetIgnore 返回值。
        bool saved = SetIgnore(vm, ignored: false);
        RefreshViews();
        AddLog(saved
            ? $"已取消忽略：{vm.Name}"
            : $"已取消忽略：{vm.Name}（⚠ 未持久化，重启后失效）");
    }

    /// <summary>批量栏：忽略所选（一次落盘，避免逐条写盘）。</summary>
    [RelayCommand]
    private void IgnoreSelected()
    {
        var targets = StorePackages.Concat(ThirdPartyPackages).Where(p => p.IsSelected).ToList();
        if (targets.Count == 0)
        {
            AddLog("忽略未执行：未勾选任何软件。");
            return;
        }

        foreach (WingetPackageVm vm in targets)
        {
            SetIgnore(vm, ignored: true, persist: false);
        }

        bool saved = PersistIgnoreList();
        RefreshViews();
        AddLog(saved
            ? $"已忽略 {targets.Count} 个软件（不再提示其更新）。"
            : $"已忽略 {targets.Count} 个软件（⚠ 未持久化，重启后失效）。");
    }

    // ==================================================================
    // 安装历史（B4-②）—— 在"完成点"追加，四个结果都记（成功/失败/取消/跳过）
    // ==================================================================

    /// <summary>把历史清单同步到界面集合（最新在前）。</summary>
    private void RefreshInstallHistory()
    {
        InstallHistory.Clear();
        foreach (InstallHistoryEntry entry in _history.Entries)
        {
            InstallHistory.Add(entry);
        }
    }

    /// <summary>追加一条安装历史（内存 + 落盘）。落盘失败只留痕，不影响主流程（历史是补充信息）。</summary>
    private void RecordInstall(
        InstallAction action,
        InstallOutcome outcome,
        WingetPackageVm pkg,
        int exitCode = 0,
        string detail = "",
        string toVersion = "")
    {
        var entry = new InstallHistoryEntry
        {
            Timestamp = DateTimeOffset.Now,
            Action = action,
            Outcome = outcome,
            PackageId = pkg.Id,
            Name = pkg.Name,
            ToVersion = toVersion,
            ExitCode = exitCode,
            Detail = detail,
        };

        _history.Append(entry);
        InstallHistory.Insert(0, entry);
        while (InstallHistory.Count > InstallHistoryLog.MaxEntries)
        {
            InstallHistory.RemoveAt(InstallHistory.Count - 1);
        }

        try
        {
            _historyStore.Save(_history);
        }
        catch (Exception ex)
        {
            _logger.Error("保存安装历史失败", ex);
        }
    }

    /// <summary>导出安装历史为 CSV（UTF-8 **带 BOM**：Excel 打开中文不乱码；原子写）。</summary>
    [RelayCommand]
    private void ExportInstallHistory()
    {
        if (InstallHistory.Count == 0)
        {
            AddLog("导出安装历史未执行：暂无记录。");
            return;
        }

        // 🟠 v18-🟠-1（2026-09-16）：`PickReportPath?.Invoke()` 移入 try —— 与 ExportList 的
        // V11-A4 修复同款（回调内部抛异常会直冲 UI 线程、用户零反馈、日志页也不新增条目）。
        // 同类只修一处：`ExportList`（AppManagerViewModel.Manual.cs）已修，本处此前未同步。
        string? path = null;
        try
        {
            path = PickReportPath?.Invoke();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            byte[] bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetBytes(InstallHistoryCsv.Build(InstallHistory));
            AtomicFile.WriteAllBytes(path, bytes);
            AddLog($"已导出安装历史（{InstallHistory.Count} 条）：{path}");
        }
        catch (Exception ex)
        {
            _logger.Error("导出安装历史失败：" + (path ?? "(未取到路径)"), ex);
            AddLog("导出安装历史失败：" + ex.Message);
        }
    }

    /// <summary>批量栏：恢复所选（取消忽略，一次落盘）。</summary>
    [RelayCommand]
    private void UnignoreSelected()
    {
        var targets = StorePackages.Concat(ThirdPartyPackages).Where(p => p.IsSelected).ToList();
        if (targets.Count == 0)
        {
            AddLog("恢复未执行：未勾选任何软件。");
            return;
        }

        foreach (WingetPackageVm vm in targets)
        {
            SetIgnore(vm, ignored: false, persist: false);
        }

        bool saved = PersistIgnoreList();
        RefreshViews();
        AddLog(saved
            ? $"已恢复 {targets.Count} 个软件（重新提示其更新）。"
            : $"已恢复 {targets.Count} 个软件（⚠ 未持久化，重启后失效）。");
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
        // 🟡 A-🟡-4（P0 已核：EnvListService 的 SaveWinget/SaveManual/SaveDriver 各自独立、失败各抛）
        // ⇒ 中途抛时**前面的已经落盘**。原实现统一报“保存软件清单失败”，用户会以为三份都没存
        // （误导性错误消息 ⇒ 状态不诚实）。改为逐条记账，如实区分“全部成功 / 部分成功”。
        string saved = string.Empty;
        string failed = string.Empty;

        void Save(string name, Action write)
        {
            try
            {
                write();
                saved = saved.Length == 0 ? name : saved + "、" + name;
            }
            catch (Exception ex)
            {
                string detail = name + "（" + ex.Message + "）";
                failed = failed.Length == 0 ? detail : failed + "；" + detail;
                _logger.Error("保存" + name + "失败", ex);
            }
        }

        Save("winget 清单", () => _env.SaveWinget(StorePackages.Concat(ThirdPartyPackages).Select(v => v.Model)));
        Save("手动清单", () => _env.SaveManual(ManualSoftwares));
        Save("驱动清单", () => _env.SaveDriver(_driverSoftwares));

        if (failed.Length == 0)
        {
            AddLog("软件清单已保存");
            return;
        }

        AddLog("⚠ 软件清单保存不完整"
            + (saved.Length == 0 ? string.Empty : "：已保存 " + saved)
            + "；失败 " + failed);
    }

    // ==================================================================
    // 日志
    // ==================================================================
    public void AddLog(string message) => LogFeed.Append(LogLines, message, MaxLogLines);

    [RelayCommand]
    private void ClearLog() => LogFeed.Clear(LogLines);

}
