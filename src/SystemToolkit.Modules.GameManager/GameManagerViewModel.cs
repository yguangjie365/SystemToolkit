using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Services;

namespace SystemToolkit.Modules.GameManager;

/// <summary>单张游戏卡 VM：展示投影 + 操作命令回调宿主。</summary>
public partial class GameCardVm : ObservableObject
{
    public SteamGame Model { get; }

    /// <remarks>封面路径由调用方在后台预计算后传入（性能审查 P1-5：构造期同步探测会阻塞 UI 线程）。</remarks>
    public GameCardVm(SteamGame model, string coverPath, GameManagerViewModel owner)
    {
        Model = model;
        Owner = owner;
        _coverPath = coverPath;
    }

    private GameManagerViewModel Owner { get; }

    public uint AppId => Model.AppId;
    public string Name => Model.Name;

    /// <summary>封面路径（可后台 CDN 补下后刷新）。</summary>
    [ObservableProperty]
    private string _coverPath;

    /// <summary>是否有封面。🔴 注意：无封面时 CoverPath 为 string.Empty（非 null）——
    /// 判空必须用 IsNullOrEmpty，写成 "is not null" 会导致占位块永不显示且绑定空串（实测踩坑）。</summary>
    public bool HasCover => !string.IsNullOrEmpty(CoverPath);

    partial void OnCoverPathChanged(string value) => OnPropertyChanged(nameof(HasCover));

    /// <summary>CDN 补下成功后由后台线程调用（ObservableProperty 已跨线程封送）。</summary>
    public void SetCover(string path) => CoverPath = path;

    /// <summary>磁盘占用（人类可读，如 "12.3 GB"）。</summary>
    public string SizeText => OverviewSizeText(Model.SizeOnDisk);

    /// <summary>游玩时长（分钟 → "X 小时 Y 分" / "X 分钟"）。</summary>
    public string PlaytimeText => Model.PlaytimeMinutes switch
    {
        0 => "从未游玩",
        < 60 => $"{Model.PlaytimeMinutes} 分钟",
        _ => $"{Model.PlaytimeMinutes / 60:0} 小时 {(int)Model.PlaytimeMinutes % 60} 分",
    };

    /// <summary>最近游玩（unix 秒 → 本地日期；从未玩过为 "—"）。</summary>
    public string LastPlayedText => Model.LastPlayed == 0
        ? "—"
        : DateTimeOffset.FromUnixTimeSeconds(Model.LastPlayed).LocalDateTime.ToString("yyyy/MM/dd");

    /// <summary>安装状态：bit2（FullyInstalled）置位 = 已安装，否则下载/更新中。</summary>
    public bool IsFullyInstalled => (Model.StateFlags & 4) != 0;
    public string StateText => IsFullyInstalled ? "已安装" : "下载/更新中";

    /// <summary>库不可用（库目录不存在，如离线盘）时为 true。</summary>
    public bool IsLibraryOffline => !Directory.Exists(Path.Combine(Model.LibraryPath, "steamapps"));

    // 排序投影（SortDescription 需要可比较属性；负号实现"降序"语义）
    public long SortRecent => -Model.LastPlayed;
    public long SortPlaytime => -(long)Model.PlaytimeMinutes;
    public long SortSize => -(long)Model.SizeOnDisk;

    public string SizeOnDiskText => OverviewSizeText(Model.SizeOnDisk);

    private static string OverviewSizeText(ulong bytes) => GameVmFormat.SizeText(bytes);
}

/// <summary>容量格式化（GameCardVm 与页头状态栏共用，避免两处实现漂移）。</summary>
internal static class GameVmFormat
{
    internal static string SizeText(ulong bytes)
    {
        double v = bytes;
        if (v >= 1024 * 1024 * 1024)
        {
            return $"{v / 1024 / 1024 / 1024:0.#} GB";
        }

        return v >= 1024 * 1024
            ? $"{v / 1024 / 1024:0} MB"
            : $"{v / 1024:0} KB";
    }
}

/// <summary>
/// 游戏管理页 VM：Steam 本地库只读展示 + 启动/商店/目录/卸载引导。
/// 数据全部来自本地 VDF/ACF（设计 §3 第一阶段策略）；加载在后台线程，一次进入页面加载一次。
/// </summary>
public partial class GameManagerViewModel : ObservableObject
{
    private readonly SteamService _steam;
    private readonly ILogger _logger;

    private readonly System.Windows.Threading.Dispatcher? _dispatcher;

    public GameManagerViewModel(SteamService steam, ILogger? logger = null, System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _steam = steam;
        _logger = logger ?? NullLogger.Instance;
        _dispatcher = dispatcher;
        GamesView = new ListCollectionView(Games)
        {
            Filter = FilterGame,
        };
    }

    /// <summary>CDN 封面补全在后台线程回调——PropertyChanged 统一编组回 UI 线程（审查 🔴-2）。</summary>
    private void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? d = _dispatcher;
        if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive || d.CheckAccess())
        {
            action();
        }
        else
        {
            _ = d.BeginInvoke(action);
        }
    }

    /// <summary>确认对话框回调（View 注入；AppManager 同款模式）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    public ObservableCollection<GameCardVm> Games { get; } = new();

    public ICollectionView GamesView { get; }

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Steam 客户端是否已安装（false 时整页走未安装空态）。默认 true：加载完成前不闪空态。</summary>
    [ObservableProperty]
    private bool _steamInstalled = true;

    partial void OnSteamInstalledChanged(bool value) => OnPropertyChanged(nameof(HeaderSubtitle));

    [ObservableProperty]
    private bool _steamRunning;

    /// <summary>当前账户显示名（loginusers.vdf MostRecent）。</summary>
    [ObservableProperty]
    private string _accountName = "—";

    /// <summary>头像字母占位（M-UI-2 审查处置：原写死 "St"；账户名有效时取首字符，无效回退 St）。</summary>
    public string AvatarInitial
    {
        get
        {
            string name = AccountName.Trim();
            return name.Length == 0 || name == "—" ? "St" : name[..1].ToUpperInvariant();
        }
    }

    partial void OnAccountNameChanged(string value) => OnPropertyChanged(nameof(AvatarInitial));

    /// <summary>底部反馈条语义级别（M-UI-2 审查处置：原单一 OnDarkMuted 无语义区分）。
    /// 0=信息（默认弱色）1=成功 2=失败。</summary>
    [ObservableProperty]
    private int _statusLevel;

    /// <summary>Steam 安装目录（封面探测用）。</summary>
    public string? SteamInstallPath { get; private set; }

    /// <summary>当前用户头像（本地 avatarcache PNG 绝对路径；缺失 null → 显示占位块）。</summary>
    [ObservableProperty]
    private string? _avatarPath;

    [ObservableProperty]
    private string _searchQuery = "";

    /// <summary>排序：0=最近游玩 1=游玩时长 2=磁盘占用 3=名称。</summary>
    [ObservableProperty]
    private int _sortMode;

    [ObservableProperty]
    private string _statusText = "尚未扫描——进入本页自动读取 Steam 库";

    /// <summary>Steam 库数量（页头副标题用；LoadAsync 完成后赋值）。</summary>
    public int LibraryCount { get; private set; }

    /// <summary>页头副标题（HTML 参考稿口径：「N 款游戏 · M 个库」）。</summary>
    public string HeaderSubtitle => SteamInstalled
        ? $"{Games.Count} 款游戏 · {LibraryCount} 个库"
        : "未检测到 Steam 客户端";

    /// <summary>内容区上方状态栏（HTML 参考稿口径：「N 款已安装 · 共占用 X GB」）。</summary>
    public string InstalledSummary
    {
        get
        {
            int installed = 0;
            ulong total = 0;
            foreach (GameCardVm g in Games)
            {
                if (g.IsFullyInstalled)
                {
                    installed++;
                }

                total += g.Model.SizeOnDisk;
            }

            return $"{installed} 款已安装 · 共占用 {GameVmFormat.SizeText(total)}";
        }
    }

    /// <summary>Games 集合或其派生统计变化后调用（刷新页头副标题与状态栏投影）。</summary>
    private void NotifySummary()
    {
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(InstalledSummary));
        OnPropertyChanged(nameof(ShowNoResultEmpty));
        OnPropertyChanged(nameof(ShowEmptyLibrary));
    }

    /// <summary>搜索无结果（有游戏数据但过滤后为空）——空态层可见性投影。</summary>
    public bool ShowNoResultEmpty => SteamInstalled && !IsLoading && Games.Count > 0 && GamesView.IsEmpty;

    /// <summary>库为空（Steam 已安装但零游戏）——空态层可见性投影。</summary>
    public bool ShowEmptyLibrary => SteamInstalled && !IsLoading && Games.Count == 0;

    partial void OnSearchQueryChanged(string value)
    {
        GamesView.Refresh();
        NotifySummary(); // 搜索无结果空态依赖过滤结果
    }

    partial void OnSortModeChanged(int value) => ApplySort();

    partial void OnIsLoadingChanged(bool value)
    {
        NotifySummary(); // 加载层/空态互斥
        LoadCommand.NotifyCanExecuteChanged(); // 审查 🔴-3：CanLoad=!IsLoading，加载态变化必须刷新按钮
    }

    [RelayCommand]
    private void SetSort(string? mode)
    {
        SortMode = int.TryParse(mode, out int v)
            ? v
            : 0;
    }

    private bool FilterGame(object item)
    {
        if (item is not GameCardVm vm)
        {
            return false;
        }

        string q = SearchQuery.Trim();
        return q.Length == 0 || vm.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        if (GamesView.SortDescriptions.Count > 0)
        {
            GamesView.SortDescriptions.Clear();
        }

        GamesView.SortDescriptions.Add(SortMode switch
        {
            1 => new SortDescription(nameof(GameCardVm.SortPlaytime), ListSortDirection.Ascending),
            2 => new SortDescription(nameof(GameCardVm.SortSize), ListSortDirection.Ascending),
            // 3=名称（审查 🔴-1 采纳：原落入 _ 分支按最近游玩排序，与 UI 选项不符）
            3 => new SortDescription(nameof(GameCardVm.Name), ListSortDirection.Ascending),
            _ => new SortDescription(nameof(GameCardVm.SortRecent), ListSortDirection.Ascending),
        });
        GamesView.Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = "正在读取 Steam 库…";
        StatusLevel = 0;
        try
        {
            SteamAllData data = await Task.Run(_steam.GetAllData).ConfigureAwait(true);

            SteamInstalled = data.InstallInfo.Installed;
            SteamRunning = data.InstallInfo.IsRunning;

            Games.Clear();
            if (SteamInstalled)
            {
                // 🔴 顺序关键：先落 Steam 安装目录，再建卡片——否则首屏封面探测拿不到主目录路径（实测踩坑）
                SteamInstallPath = data.InstallInfo.InstallPath;

                // 封面探测并行预计算（性能审查 P1-5）：每卡最多 9 次候选路径探测 × N 卡，
                // 原实现在 UI 线程逐卡同步探测，200 卡 = 上千次同步文件打开阻塞首屏
                string installPath = SteamInstallPath ?? string.Empty;
                System.Collections.Concurrent.ConcurrentDictionary<uint, string> coverPaths = await Task.Run(() =>
                {
                    var map = new System.Collections.Concurrent.ConcurrentDictionary<uint, string>();
                    Parallel.ForEach(data.Games, new ParallelOptions { MaxDegreeOfParallelism = 4 }, g =>
                    {
                        // 审查 🔴-4：单张封面探测失败不得炸掉整个加载（AggregateException → 全量失败）
                        try
                        {
                            string? found = SteamService.FindCoverArt(installPath, g.LibraryPath, g.AppId);
                            if (found is not null)
                            {
                                map[g.AppId] = found;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"封面探测失败（AppId {g.AppId}）：{ex.Message}");
                        }
                    });
                    return map;
                }).ConfigureAwait(true);

                foreach (SteamGame g in data.Games)
                {
                    string cover = coverPaths.TryGetValue(g.AppId, out string? cp) ? cp : string.Empty;
                    var vm = new GameCardVm(g, cover, this);
                    HookFilterRefresh(vm);
                    Games.Add(vm);
                }

                SteamUser? activeUser = data.Users.FirstOrDefault(u => u.MostRecent) ?? data.Users.FirstOrDefault();
                AccountName = activeUser?.PersonaName ?? "—";
                string? mostRecentId = activeUser?.SteamId64;
                // 头像走 ObservableProperty 赋值 → 触发 UI 变更通知
                AvatarPath = mostRecentId is null ? null : _steam.GetAvatarPath(mostRecentId);
                LibraryCount = data.Libraries.Count;
            }

            ApplySort();
            NotifySummary(); // 页头副标题 + 状态栏统计依赖 Games/LibraryCount，集合填充后统一刷新

            // CDN 封面兜底：本地缺失的卡片后台逐个补下（10s 超时/张，失败静默占位），下载成功渐进刷新
            var coverless = Games.Where(g => !g.HasCover).ToList();
            if (coverless.Count > 0)
            {
                string cacheDir = Path.Combine(
                    System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SystemToolkit", "cache", "steam-covers");
                // REVIEW-3 A-2：串行逐张（每张 10s 超时）在缺封面多时补全过慢（50 张 ≈ 8 分钟），
                // 改限并发 3——对 Steam CDN 保持礼貌，同时把最坏等待压到 ~1/3
                _ = Task.Run(async () =>
                {
                    var gate = new System.Threading.SemaphoreSlim(3);
                    await Task.WhenAll(coverless.Select(async vm =>
                    {
                        try
                        {
                            await gate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                string? path = await SteamService.EnsureCoverFromCdnAsync(cacheDir, vm.AppId)
                                    .ConfigureAwait(false);
                                if (path is not null)
                                    RunOnUi(() => vm.SetCover(path)); // 审查 🔴-2：后台线程 PropertyChanged 统一编组
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }
                        catch
                        {
                            // 单张失败静默（无网/超时），保留占位
                        }
                    })).ConfigureAwait(false);
                });
            }
            GamesView.Refresh();
            StatusLevel = 0;
            StatusText = SteamInstalled
                ? $"找到 {Games.Count} 款游戏 · 库 {data.Libraries.Count} 个 · Steam {(SteamRunning ? "运行中" : "未运行")}"
                : "未检测到 Steam 客户端";
            _logger.Info($"游戏库加载完成：{Games.Count} 款（SteamInstalled={SteamInstalled}）");
        }
        catch (Exception ex)
        {
            StatusText = "Steam 库读取失败：" + ex.Message;
            StatusLevel = 2;
            _logger.Error("Steam 库读取失败", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLoad => !IsLoading;

    private void HookFilterRefresh(GameCardVm vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameCardVm.StateText) or nameof(GameCardVm.IsLibraryOffline))
            {
                GamesView.Refresh();
            }
        };
    }

    [RelayCommand]
    private void LaunchGame(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        bool ok = _steam.LaunchGame(vm.AppId);
        StatusText = ok ? $"已请求启动：{vm.Name}" : $"启动失败：{vm.Name}（steam:// 协议调用失败）";
        StatusLevel = ok ? 1 : 2;
        _logger.Info($"启动游戏 {vm.Name}({vm.AppId})：{ok}");
    }

    [RelayCommand]
    private void OpenStore(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        bool ok = _steam.OpenStorePage(vm.AppId);
        StatusText = ok ? $"已打开商店页：{vm.Name}" : $"打开商店页失败：{vm.Name}";
        StatusLevel = ok ? 1 : 2;
        _logger.Info($"打开商店页 {vm.Name}：{ok}");
    }

    [RelayCommand]
    private void OpenFolder(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        bool ok = _steam.OpenGameFolder(vm.Model.LibraryPath, vm.Model.InstallDir);
        StatusText = ok ? $"已打开安装目录：{vm.Name}" : $"安装目录不存在：{vm.Name}（可能库离线）";
        StatusLevel = ok ? 1 : 2;
        _logger.Info($"打开安装目录 {vm.Name}：{ok}");
    }

    /// <summary>卸载引导：steam://uninstall 协议拉起 Steam 自身卸载流程（不直接删文件，设计 §6）。</summary>
    [RelayCommand]
    private void UninstallGame(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        if (ConfirmRequest?.Invoke(
                "卸载游戏",
                $"将通过 Steam 卸载：{vm.Name}\n（占用 {vm.SizeOnDiskText}）\n\n"
                + "会拉起 Steam 官方卸载流程，需要在该流程中再次确认。\n确定继续吗？") != true)
        {
            _logger.Info($"已取消卸载：{vm.Name}");
            return;
        }

        bool ok = _steam.UninstallGame(vm.AppId);
        StatusText = ok ? $"已提交卸载请求：{vm.Name}（请在 Steam 窗口中确认）" : $"卸载请求失败：{vm.Name}";
        StatusLevel = ok ? 1 : 2;
        _logger.Info($"卸载游戏 {vm.Name}：{ok}");
    }

    [RelayCommand]
    private void LaunchClient()
    {
        bool ok = _steam.LaunchClient();
        StatusText = ok ? "已请求启动 Steam" : "启动 Steam 失败（未找到 steam.exe）";
        StatusLevel = ok ? 1 : 2;
        _logger.Info($"启动 Steam 客户端：{ok}");
    }
}
