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

    /// <remarks>封面路径与库离线态均由调用方在后台预计算后传入（性能审查 R3：每回收重算 Directory.Exists 会阻塞 UI 线程）。</remarks>
    public GameCardVm(SteamGame model, string coverPath, bool libraryOffline, GameManagerViewModel owner)
    {
        Model = model;
        Owner = owner;
        _coverPath = coverPath;
        _isLibraryOffline = libraryOffline;
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

    /// <summary>库不可用（库目录不存在，如离线盘）时为 true。审查 R3：载入期按库路径预计算一次，避免每次容器回收重发 Directory.Exists。</summary>
    private readonly bool _isLibraryOffline;
    public bool IsLibraryOffline => _isLibraryOffline;

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
/// 账户下拉里的一项（A1 账户选择器，2026-09-13）：头像 + 显示名 + 是否当前账户。
/// <para>
/// 🔴 切换命令放在**项自身**而非页 VM：<c>ContextMenu</c> 属独立可视树，项模板内用
/// <c>RelativeSource FindAncestor</c> 回不到页 VM（本文件卡片 ⋯ 菜单靠
/// <c>PlacementTarget.Tag</c> 桥接，但那是"单容器"场景，ItemsSource 生成的多个容器不适用）。
/// </para>
/// </summary>
public partial class SteamAccountVm : ObservableObject
{
    private readonly GameManagerViewModel _owner;

    /// <summary>构造。头像路径与"是否当前"由页 VM 在后台预计算后传入（沿用 GameCardVm 同款做法）。</summary>
    public SteamAccountVm(SteamUser model, bool isCurrent, string? avatarPath, GameManagerViewModel owner)
    {
        Model = model;
        _isCurrent = isCurrent;
        _avatarPath = avatarPath;
        _owner = owner;
    }

    /// <summary>底层模型。⚠️ 切换用 <see cref="SteamUser.AccountName"/>（登录名），**不是** PersonaName（昵称）。</summary>
    public SteamUser Model { get; }

    public string SteamId64 => Model.SteamId64;

    /// <summary>显示名：PersonaName（用户昵称）优先，空则回退 AccountName。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Model.PersonaName) ? Model.AccountName : Model.PersonaName;

    /// <summary>头像字母占位（与页头 <c>AvatarInitial</c> 同一规则：取首字符大写，空回退 "St"）。</summary>
    public string Initial
    {
        get
        {
            string n = DisplayName.Trim();
            return n.Length == 0 ? "St" : n[..1].ToUpperInvariant();
        }
    }

    /// <summary>当前登录账户（菜单里标记「当前」）。切换成功后由页 VM 就地重标，不重扫游戏库。</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>本地头像绝对路径；缺失为 null。</summary>
    [ObservableProperty]
    private string? _avatarPath;

    partial void OnAvatarPathChanged(string? value) => OnPropertyChanged(nameof(HasAvatar));

    /// <summary>🔴 无头像时 AvatarPath 为 null（非空串），但**判空必须用 IsNullOrEmpty**——
    /// 与 GameCardVm 同款坑，写成 "is not null" 会在空串时误判为有头像。</summary>
    public bool HasAvatar => !string.IsNullOrEmpty(AvatarPath);

    /// <summary>
    /// 切换到本账户。命令体**顶层** catch —— <c>AsyncRelayCommand</c> 会吞异常
    /// （守卫 <c>AsyncCommandCatchGuardTests</c> 基线制，判据为花括号深度 == 1）。
    /// </summary>
    [RelayCommand]
    private async Task SwitchAsync()
    {
        try
        {
            await _owner.SwitchAccountAsync(this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _owner.ReportAccountSwitchFailure(DisplayName, ex);
        }
    }
}

/// <summary>
/// 游戏管理页 VM：Steam 本地库只读展示 + 启动/商店/目录/卸载引导 + 账户切换（A1）。
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

    // ================= A1 账户选择器（2026-09-13） =================

    /// <summary>记住的 Steam 账户（下拉项；含头像与"当前"标记）。</summary>
    public ObservableCollection<SteamAccountVm> Accounts { get; } = new();

    /// <summary>有可切换的账户（下拉按钮可用性）。账户集合变化后由 <see cref="NotifyAccountsChanged"/> 手动通知。</summary>
    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>账户切换进行中（切换会关停 Steam，最长 10s；期间禁止再次触发）。</summary>
    [ObservableProperty]
    private bool _isSwitchingAccount;

    partial void OnIsSwitchingAccountChanged(bool value) => OnPropertyChanged(nameof(CanPickAccount));

    /// <summary>账户下拉可点（有账户且不在切换中）。禁用时由 View 的 ToolTip 说明原因。</summary>
    public bool CanPickAccount => HasAccounts && !IsSwitchingAccount;

    /// <summary>账户集合重建后调用（ObservableCollection 不通知 Count 派生属性）。</summary>
    private void NotifyAccountsChanged()
    {
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(CanPickAccount));
    }

    // ================= A3 库容量（2026-09-13） =================

    /// <summary>库容量摘要（「· 库 N 个 · 剩余 X GB」）。空串 = 无可展示内容（View 隐藏该段）。</summary>
    [ObservableProperty]
    private string _libraryCapacityText = string.Empty;

    partial void OnLibraryCapacityTextChanged(string value) => OnPropertyChanged(nameof(HasLibraryCapacity));

    /// <summary>有库容量可展示。</summary>
    public bool HasLibraryCapacity => LibraryCapacityText.Length > 0;

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
                (System.Collections.Concurrent.ConcurrentDictionary<uint, string> coverPaths, HashSet<string> offlineLibs) = await Task.Run(() =>
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

                    // 审查 R3：按库路径（数量极少）各一次 Directory.Exists，离线/网络盘判定离 UI 线程预计算
                    var offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string lib in data.Games.Select(g => g.LibraryPath).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (!Directory.Exists(Path.Combine(lib, "steamapps")))
                            {
                                offline.Add(lib);
                            }
                        }
                        catch
                        {
                            offline.Add(lib); // 探测异常视为不可用
                        }
                    }

                    return (map, offline);
                }).ConfigureAwait(true);

                foreach (SteamGame g in data.Games)
                {
                    string cover = coverPaths.TryGetValue(g.AppId, out string? cp) ? cp : string.Empty;
                    var vm = new GameCardVm(g, cover, offlineLibs.Contains(g.LibraryPath), this);
                    HookFilterRefresh(vm);
                    Games.Add(vm);
                }

                SteamUser? activeUser = data.Users.FirstOrDefault(u => u.MostRecent) ?? data.Users.FirstOrDefault();
                AccountName = activeUser?.PersonaName ?? "—";
                string? mostRecentId = activeUser?.SteamId64;
                // 头像走 ObservableProperty 赋值 → 触发 UI 变更通知
                AvatarPath = mostRecentId is null ? null : _steam.GetAvatarPath(mostRecentId);
                LibraryCount = data.Libraries.Count;
                BuildAccounts(data.Users, activeUser);        // A1
                UpdateLibraryCapacity(data.Libraries);        // A3
            }
            else
            {
                // Steam 卸载/不可用：清干净，避免上一轮的值残留（账户名/头像/容量/库数）
                Accounts.Clear();
                NotifyAccountsChanged();
                LibraryCapacityText = string.Empty;
                AccountName = "—";
                AvatarPath = null;
                LibraryCount = 0;
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
                    // 审查 🟠-6 采纳：单张静默改为计数 + 收尾汇总一条日志——
                    // 逐张记日志会在断网时刷出上百条，汇总既留痕又不淹没日志
                    int failed = 0;
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
                        catch (Exception ex)
                        {
                            System.Threading.Interlocked.Increment(ref failed);
                            System.Diagnostics.Debug.WriteLine($"[Game] CDN 封面补全失败（AppId {vm.AppId}）：{ex.Message}");
                        }
                    })).ConfigureAwait(false);

                    if (failed > 0)
                    {
                        _logger.Warn($"[游戏] CDN 封面补全失败 {failed}/{coverless.Count} 张（无网或超时），已保留占位图");
                    }
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

    // ================= A1 账户：构建与切换 =================

    /// <summary>
    /// 构建账户下拉项。
    /// <para>
    /// 头像走 <see cref="SteamService.GetAvatarPath"/>（**路径式** API，WPF 直接绑 Image.Source）。
    /// ⚠️ 不用 <c>FetchUserAvatarsAsync</c>：它返回 base64 data URI（Blazor 时代的形状），
    /// WPF 侧还得再解码成 BitmapImage，徒增内存与代码。
    /// </para>
    /// </summary>
    /// <remarks>internal + InternalsVisibleTo：直测覆盖"当前项标记"与 0/1/N 账户（UI 手点验不出来）。</remarks>
    internal void BuildAccounts(IReadOnlyList<SteamUser> users, SteamUser? active)
    {
        Accounts.Clear();
        foreach (SteamUser u in users)
        {
            bool isCurrent = active is not null
                && string.Equals(u.SteamId64, active.SteamId64, StringComparison.Ordinal);
            Accounts.Add(new SteamAccountVm(u, isCurrent, _steam.GetAvatarPath(u.SteamId64), this));
        }

        NotifyAccountsChanged();
    }

    /// <summary>
    /// 切换到指定账户（A1）。
    /// 🔴 <c>SteamService.SwitchAccount</c> 内部会 taskkill Steam 并**最长阻塞 10s**
    /// （<c>KillSteamAndWait</c>）→ 必须离 UI 线程，否则页面冻结 10 秒。
    /// 属"修改类"操作：确认门 + 文案讲清后果（会重启 Steam、改写 loginusers.vdf）。
    /// </summary>
    internal async Task SwitchAccountAsync(SteamAccountVm target)
    {
        if (IsSwitchingAccount)
        {
            return; // 防连点（按钮已由 CanPickAccount 禁用，这里是兜底）
        }

        if (target.IsCurrent)
        {
            StatusText = $"已是当前账户：{target.DisplayName}";
            StatusLevel = 0;
            return;
        }

        // 切换走的是 AccountName（登录名）；Model 上缺失则中止——写进 vdf 会造出坏条目
        string accountName = target.Model.AccountName;
        if (string.IsNullOrWhiteSpace(accountName))
        {
            StatusText = $"账户 {target.DisplayName} 缺少登录名，无法切换";
            StatusLevel = 2;
            _logger.Warn($"[游戏] 账户 SteamId={target.SteamId64} 无 AccountName，切换中止");
            return;
        }

        if (ConfirmRequest?.Invoke(
                "切换 Steam 账户",
                $"将切换到：{target.DisplayName}\n\n"
                + "此操作会：\n"
                + "· 关闭正在运行的 Steam（进行中的下载/上传会中断）\n"
                + "· 改写 Steam 登录配置 loginusers.vdf（自动留 .bak 备份）\n"
                + "· 以该账户重新启动 Steam\n\n"
                + "确定继续吗？") != true)
        {
            _logger.Info($"已取消切换账户：{target.DisplayName}");
            StatusText = $"已取消切换账户：{target.DisplayName}";
            StatusLevel = 0;
            return;
        }

        IsSwitchingAccount = true;
        StatusText = $"正在切换到 {target.DisplayName}…（Steam 将被重启）";
        StatusLevel = 0;
        try
        {
            bool ok = await Task.Run(() => _steam.SwitchAccount(accountName)).ConfigureAwait(true);
            if (ok)
            {
                AccountName = target.DisplayName;
                AvatarPath = target.AvatarPath;
                foreach (SteamAccountVm a in Accounts)
                {
                    a.IsCurrent = ReferenceEquals(a, target);
                }

                // 实测进程态，而不是凭"我们刚启动过"下断言
                SteamRunning = _steam.IsClientRunning();
                StatusText = $"已切换到 {target.DisplayName}，Steam 正在以该账户启动";
                StatusLevel = 1;
            }
            else
            {
                StatusText = $"切换账户失败：{target.DisplayName}（详见日志）";
                StatusLevel = 2;
            }

            _logger.Info($"切换账户 → {target.DisplayName}({accountName})：{ok}");
        }
        catch (Exception ex)
        {
            StatusText = "切换账户异常：" + ex.Message;
            StatusLevel = 2;
            _logger.Error("切换账户异常", ex);
        }
        finally
        {
            IsSwitchingAccount = false;
        }
    }

    /// <summary>账户项命令的兜底出口（项 VM 的 catch 分支调用；异常不得静默）。</summary>
    internal void ReportAccountSwitchFailure(string displayName, Exception ex)
    {
        StatusText = $"切换账户异常：{displayName}";
        StatusLevel = 2;
        _logger.Error($"[游戏] 切换账户异常（{displayName}）", ex);
    }

    // ================= A3 库容量 =================

    /// <summary>
    /// 库容量摘要。
    /// ⚠️ 剩余空间按**卷根去重**累加：同一分区上可以有多个库（多个 libraryfolders 条目指向同一盘），
    /// 直接逐库累加会把同一块可用空间算多次。卷未就绪/网络盘时 <c>FreeSize</c> 为 0（FillDriveSize 吞异常），
    /// 此时该卷不计入，但库数照报。
    /// </summary>
    /// <remarks>internal + InternalsVisibleTo：直测覆盖"同卷多库去重"（最易写错的一处）。</remarks>
    internal void UpdateLibraryCapacity(IReadOnlyList<SteamLibrary> libraries)
    {
        if (libraries.Count == 0)
        {
            LibraryCapacityText = string.Empty;
            return;
        }

        var freeByRoot = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (SteamLibrary lib in libraries)
        {
            if (lib.FreeSize == 0)
            {
                continue;
            }

            string root = Path.GetPathRoot(lib.Path) ?? lib.Path;
            freeByRoot[root] = lib.FreeSize; // 同卷各库取值相同，覆盖无副作用
        }

        if (freeByRoot.Count == 0)
        {
            LibraryCapacityText = $"· 库 {libraries.Count} 个";
            return;
        }

        ulong free = 0;
        foreach (ulong v in freeByRoot.Values)
        {
            free += v;
        }

        LibraryCapacityText = $"· 库 {libraries.Count} 个 · 剩余 {GameVmFormat.SizeText(free)}";
    }

    // ================= A4 诊断导出：❌ 已撤下 =================
    // 2026-09-13 主人反馈：页头放「导出诊断」按钮不合适，删除该功能（VM 命令 + View 按钮一并移除）。
    // 未实现的迁移方案（如将来需要）：挂到设置模块或菜单里，而不是占页头主操作位。

    /// <summary>
    /// 同步命令统一兜底（审查 🔴 采纳，2026-09-09）：steam:// 协议调用 / 进程启动会因协议未注册、
    /// 路径失效抛 Win32Exception 等——同步命令没有 AsyncRelayCommand 的兜底层，异常会直冲 UI 线程
    /// 触发未处理异常（崩溃风险）。统一在此捕获：状态栏可见 + 日志留痕（🔴 不静默）。
    /// </summary>
    private void Guard(string action, Action run)
    {
        try
        {
            run();
        }
        catch (Exception ex)
        {
            StatusText = $"{action}异常：{ex.Message}";
            StatusLevel = 2;
            _logger.Error($"[游戏] {action}异常：{ex.Message}", ex);
        }
    }

    [RelayCommand]
    private void LaunchGame(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        Guard($"启动《{vm.Name}》", () =>
        {
            bool ok = _steam.LaunchGame(vm.AppId);
            StatusText = ok ? $"已请求启动：{vm.Name}" : $"启动失败：{vm.Name}（steam:// 协议调用失败）";
            StatusLevel = ok ? 1 : 2;
            _logger.Info($"启动游戏 {vm.Name}({vm.AppId})：{ok}");
        });
    }

    [RelayCommand]
    private void OpenStore(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        Guard($"打开商店页《{vm.Name}》", () =>
        {
            bool ok = _steam.OpenStorePage(vm.AppId);
            StatusText = ok ? $"已打开商店页：{vm.Name}" : $"打开商店页失败：{vm.Name}";
            StatusLevel = ok ? 1 : 2;
            _logger.Info($"打开商店页 {vm.Name}：{ok}");
        });
    }

    [RelayCommand]
    private void OpenFolder(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        Guard($"打开安装目录《{vm.Name}》", () =>
        {
            bool ok = _steam.OpenGameFolder(vm.Model.LibraryPath, vm.Model.InstallDir);
            StatusText = ok ? $"已打开安装目录：{vm.Name}" : $"安装目录不存在：{vm.Name}（可能库离线）";
            StatusLevel = ok ? 1 : 2;
            _logger.Info($"打开安装目录 {vm.Name}：{ok}");
        });
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

        Guard($"卸载《{vm.Name}》", () =>
        {
            bool ok = _steam.UninstallGame(vm.AppId);
            StatusText = ok ? $"已提交卸载请求：{vm.Name}（请在 Steam 窗口中确认）" : $"卸载请求失败：{vm.Name}";
            StatusLevel = ok ? 1 : 2;
            _logger.Info($"卸载游戏 {vm.Name}：{ok}");
        });
    }

    [RelayCommand]
    private void LaunchClient()
    {
        Guard("启动 Steam 客户端", () =>
        {
            bool ok = _steam.LaunchClient();
            StatusText = ok ? "已请求启动 Steam" : "启动 Steam 失败（未找到 steam.exe）";
            StatusLevel = ok ? 1 : 2;
            _logger.Info($"启动 Steam 客户端：{ok}");
        });
    }
}
