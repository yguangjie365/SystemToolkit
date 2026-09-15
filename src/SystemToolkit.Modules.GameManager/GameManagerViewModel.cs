using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Online;
using SystemToolkit.Core.GameManager.Services;

namespace SystemToolkit.Modules.GameManager;

/// <summary>
/// 卡片状态（五态互斥；<b>判据优先序即声明序</b>：未安装 &gt; 库离线 &gt; 需更新 &gt; 下载/更新中 &gt; 已安装）。
/// </summary>
public enum GameCardState
{
    /// <summary>安装完整且库可用（bit2 置位、bit1 未置位）。</summary>
    Installed,

    /// <summary>正在下载/更新（<c>.acf</c> 的 StateFlags bit2 未置位）。</summary>
    Downloading,

    /// <summary>已安装但有更新待下（bit2 与 bit1 同置，即实测的 <c>6</c>）。</summary>
    NeedsUpdate,

    /// <summary>库目录不可用（离线盘 / 网络盘）。</summary>
    LibraryOffline,

    /// <summary>本地未安装（有游玩记录但 <c>.acf</c> 不存在；B2 库存条目）。</summary>
    NotInstalled,
}

/// <summary>单张游戏卡 VM：展示投影 + 操作命令回调宿主。</summary>
public partial class GameCardVm : ObservableObject
{
    /// <summary>
    /// 库存条目（B2 起为 <see cref="SteamInventoryGame"/>：它是 <c>.acf</c> 已安装清单的**超集**，
    /// 额外带 <c>Installed</c> 标记，卡片才能表达「玩过但已卸载」）。
    /// </summary>
    public SteamInventoryGame Model { get; }

    /// <remarks>封面路径与库离线态均由调用方在后台预计算后传入（性能审查 R3：每回收重算 Directory.Exists 会阻塞 UI 线程）。</remarks>
    public GameCardVm(SteamInventoryGame model, string coverPath, bool libraryOffline, GameManagerViewModel owner)
    {
        Model = model;
        Owner = owner;
        _coverPath = coverPath;
        _isLibraryOffline = libraryOffline;
    }

    private GameManagerViewModel Owner { get; }

    public uint AppId => Model.AppId;

    /// <summary>
    /// 显示名：**优先中文名**（<c>appinfo.vdf</c> 的 <c>name_localized</c>），无中文名时用原名。
    /// <para>
    /// 🔴 <c>Model.Name</c> 恒为英文原名（<c>.acf</c> / <c>common.name</c> 都不带语言信息），
    /// 所以「Steam 里显示中文、本软件显示英文」只能在这里修（2026-09-13 实机反馈）。
    /// </para>
    /// </summary>
    public string Name => Model.NameZh.Length > 0 ? Model.NameZh : Model.Name;

    /// <summary>英文原名（搜索时与 <see cref="Name"/> 一起参与匹配——用户可能按原名找）。</summary>
    public string NameOriginal => Model.Name;

    /// <summary>封面路径（可后台 CDN 补下后刷新）。</summary>
    [ObservableProperty]
    private string _coverPath;

    /// <summary>是否有封面。🔴 注意：无封面时 CoverPath 为 string.Empty（非 null）——
    /// 判空必须用 IsNullOrEmpty，写成 "is not null" 会导致占位块永不显示且绑定空串（实测踩坑）。</summary>
    public bool HasCover => !string.IsNullOrEmpty(CoverPath);

    partial void OnCoverPathChanged(string value) => OnPropertyChanged(nameof(HasCover));

    /// <summary>CDN 补下成功后由后台线程调用（ObservableProperty 已跨线程封送）。</summary>
    public void SetCover(string path) => CoverPath = path;

    /// <summary>磁盘占用（人类可读，如 "12.3 GB"）。未安装返回 <see cref="NotInstalledPlaceholder"/>。</summary>
    public string SizeText => Model.Installed ? OverviewSizeText(Model.SizeOnDisk) : NotInstalledPlaceholder;

    /// <summary>游玩时长（分钟 → "X 小时 Y 分" / "X 分钟"）。</summary>
    public string PlaytimeText => Model.PlaytimeMinutes switch
    {
        0 => "从未游玩",
        < 60 => $"{Model.PlaytimeMinutes} 分钟",
        _ => $"{Model.PlaytimeMinutes / 60:0} 小时 {(int)Model.PlaytimeMinutes % 60} 分",
    };

    /// <summary>
    /// 卡片 meta 行用的**紧凑时长**（拉丁单位，与同一行的「49.9 GB」风格一致）。
    /// <para>
    /// 🔴 单独一个属性而不是改 <see cref="PlaytimeText"/>：详情面板在宽敞的两列布局里显示
    /// 「86 小时 43 分」更友好；卡片 meta 行是 mono 微字、与「GB」挤在同一行，那里才有
    /// 「GB 与 小时/分 中英混排」的问题（2026-09-13 UI 评审）。
    /// </para>
    /// </summary>
    public string PlaytimeShortText => Model.PlaytimeMinutes switch
    {
        0 => "0h",
        < 60 => $"{Model.PlaytimeMinutes}m",
        _ when Model.PlaytimeMinutes % 60 == 0 => $"{Model.PlaytimeMinutes / 60}h",
        _ => $"{Model.PlaytimeMinutes / 60:0}h{Model.PlaytimeMinutes % 60:00}m",
    };

    /// <summary>最近游玩（unix 秒 → 本地日期；从未玩过为 "—"）。</summary>
    public string LastPlayedText => Model.LastPlayed == 0
        ? "—"
        : DateTimeOffset.FromUnixTimeSeconds(Model.LastPlayed).LocalDateTime.ToString("yyyy/MM/dd");

    /// <summary>本地已安装（来自库存条目的 <c>Installed</c> 标记）。</summary>
    public bool IsInstalled => Model.Installed;

    /// <summary>未安装（View 据此显示「未安装」徽章、并隐藏对未安装项无意义的操作）。</summary>
    public bool IsNotInstalled => !Model.Installed;

    /// <summary>
    /// 安装完整（Steam <c>EAppState</c>：bit2「FullyInstalled」置位，且 bit1「UpdateRequired」**未**置位）。
    /// <para>
    /// 🔴 bit1 必须一起判：<c>6 = 110b</c> 是「已安装 **但待更新**」，只看 bit2 会把它说成「已安装」——
    /// 等于告诉用户"这就是最新的"，是**状态欺骗**（本机实测该值确实出现在库中，2026-09-13 收紧）。
    /// </para>
    /// </summary>
    public bool IsFullyInstalled => (Model.StateFlags & 4) != 0 && (Model.StateFlags & 2) == 0;

    /// <summary>已安装但有更新待下（bit2 与 bit1 同置；与 <see cref="IsFullyInstalled"/> 互斥）。</summary>
    public bool NeedsUpdate => Model.Installed && (Model.StateFlags & 4) != 0 && (Model.StateFlags & 2) != 0;

    /// <summary>
    /// 「进度类」徽章可见性（下载/更新中 或 需更新）。
    /// <para>
    /// 🔴 徽章可见性必须绑**状态判据本身**，不能绑 "IsFullyInstalled 取反" 这类**泛化状态位**：
    /// 未安装条目的 <c>StateFlags=0</c> 也让 bit2 为假 → 泛化位会同时点亮「未安装」与「下载/更新中」
    /// 两块叠加徽章（实测 2026-09-13：封面遮罩是半透明渐变 `#80000000`，被压住的那块会**透出来**）。
    /// </para>
    /// </summary>
    public bool ShowProgressBadge => StateKind is GameCardState.Downloading or GameCardState.NeedsUpdate;

    /// <summary>
    /// 五态判据（互斥，优先序同 <see cref="GameCardState"/> 声明序）。
    /// 🔴 <b>未安装必须最先判</b>：未安装条目的 <c>StateFlags=0</c>，若先判 bit2 会落到
    /// 「下载/更新中」——那是**错误文案**（告诉用户一个没发生的事实）。
    /// </summary>
    public GameCardState StateKind
    {
        get
        {
            if (!Model.Installed)
            {
                return GameCardState.NotInstalled;
            }

            if (IsLibraryOffline)
            {
                return GameCardState.LibraryOffline;
            }

            if (NeedsUpdate)
            {
                return GameCardState.NeedsUpdate;
            }

            return IsFullyInstalled ? GameCardState.Installed : GameCardState.Downloading;
        }
    }

    /// <summary>状态文案（与 <see cref="StateKind"/> 一一对应——文案不许在别处再写一份）。</summary>
    public string StateText => StateKind switch
    {
        GameCardState.NotInstalled => "未安装",
        GameCardState.LibraryOffline => "库离线",
        GameCardState.NeedsUpdate => "需更新",
        GameCardState.Downloading => "下载/更新中",
        _ => "已安装",
    };

    /// <summary>未安装条目的数值占位符（不显示 <c>0 KB</c>——那会把"没装"说成"装了但极小"）。</summary>
    internal const string NotInstalledPlaceholder = "—";

    /// <summary>库不可用（库目录不存在，如离线盘）时为 true。审查 R3：载入期按库路径预计算一次，避免每次容器回收重发 Directory.Exists。</summary>
    private readonly bool _isLibraryOffline;
    public bool IsLibraryOffline => _isLibraryOffline;

    // 排序投影（SortDescription 需要可比较属性；负号实现"降序"语义）
    public long SortRecent => -Model.LastPlayed;
    public long SortPlaytime => -(long)Model.PlaytimeMinutes;
    public long SortSize => -(long)Model.SizeOnDisk;

    public string SizeOnDiskText => Model.Installed ? OverviewSizeText(Model.SizeOnDisk) : NotInstalledPlaceholder;

    // ================= A2 详情面板投影（2026-09-13） =================

    /// <summary>AppID 文本（详情面板展示用；uint 无千分位，纯数字）。</summary>
    public string AppIdText => AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 安装目录完整路径（详情面板展示用）。拼法与 <c>SteamService.OpenGameFolder</c> 保持一致
    /// （<c>库路径\steamapps\common\安装子目录</c>）——否则会出现「面板显示的路径」与
    /// 「点『打开目录』实际打开的路径」不一致的状态欺骗。缺任一段时返回空串，View 隐藏该行。
    /// </summary>
    public string InstallPathText =>
        string.IsNullOrEmpty(Model.LibraryPath) || string.IsNullOrEmpty(Model.InstallDir)
            ? string.Empty
            : Path.Combine(Model.LibraryPath, "steamapps", "common", Model.InstallDir);

    /// <summary>有可展示的安装路径（View 用它隐藏空行，不显示半截路径）。</summary>
    public bool HasInstallPath => InstallPathText.Length > 0;

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
    private readonly ISteamApiKeyStore? _apiKeyStore;

    private readonly System.Windows.Threading.Dispatcher? _dispatcher;

    public GameManagerViewModel(
        SteamService steam,
        ILogger? logger = null,
        System.Windows.Threading.Dispatcher? dispatcher = null,
        ISteamApiKeyStore? apiKeyStore = null)
    {
        _steam = steam;
        _logger = logger ?? NullLogger.Instance;
        _dispatcher = dispatcher;
        _apiKeyStore = apiKeyStore;
        ApiKeyConfigured = apiKeyStore?.Get() is not null;
        GamesView = new ListCollectionView(Games)
        {
            Filter = FilterGame,
        };
    }

    /// <summary>CDN 封面补全在后台线程回调——PropertyChanged 统一编组回 UI 线程（审查 🔴-2）。</summary>
    private void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? d = _dispatcher;
        // 🟡 X-2（两批审查，5 处同款，经评估**保持现状**）：`d is null` 时直执行是**有意**的——
        // ① 生产路径 `Application.Current?.Dispatcher` 在 App.OnStartup（UI 线程）内注入，非 null；
        // ② `SystemToolkit.Worker` 目前是 stub，不构造任何模块 VM ⇒ 生产上走不到本分支；
        // ③ 测试宿主**刻意**传 null（全仓 9 处）——拒绝执行会让 VM 状态永不更新、测试无从断言。
        // 🔴 未来若 worker 化落地（无 UI 线程构造 VM），本分支才真正危险：
        //    届时后台线程会**直接改 UI 集合**（ObservableCollection 跨线程）。改法是拒绝执行并落日志，
        //    但必须**同时**给测试宿主一条有 Dispatcher 的通道（否则现有 9 处用例全红）。
        if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive || d.CheckAccess())
        {
            RunGuarded(action);
        }
        else
        {
            _ = d.BeginInvoke(() => RunGuarded(action));
        }
    }

    /// <summary>
    /// 🟠 V13-G3（2026-09-14 审查）：编组入口的两条执行分支统一兜底（对齐
    /// <c>MusicManagerViewModel.RunGuarded</c> 同名实现）。原实现两条分支都无 try/catch：
    /// 后台直执行分支的异常会被 <c>App.xaml.cs</c> 的 <c>TaskScheduler.UnobservedTaskException</c>
    /// 接住，但该事件**只在 Task 被 GC 终结时触发**（时点不确定，可能直到退出都不触发）且无 AppLog/UI 出口；
    /// <c>BeginInvoke</c> 分支则直冲 <c>DispatcherUnhandledException</c>。两者应用内都不可见。
    /// <para>⚠️ 与 Music 同名实现的差异：此处**只落日志不改状态栏**——本方法会被封面补全的后台线程直接调用
    /// （测试宿主 dispatcher 为 null 时走直执行分支），跨线程写 <c>StatusText</c> 属 UI 线程资源。</para>
    /// </summary>
    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Error($"[游戏] UI 回调异常：{ex.Message}", ex);
        }
    }

    /// <summary>确认对话框回调（View 注入；AppManager 同款模式）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    public ObservableCollection<GameCardVm> Games { get; } = new();

    public ICollectionView GamesView { get; }

    // ================= A4 API Key（批次 4 在线库存，2026-09-13） =================

    /// <summary>
    /// 是否已配置 Steam Web API Key（驱动页头按钮文案）。
    /// <para>🔴 只暴露**是否已设置**，不把 Key 本身放到可绑定属性上——避免明文进入绑定/日志/诊断导出。</para>
    /// </summary>
    [ObservableProperty]
    private bool _apiKeyConfigured;

    partial void OnApiKeyConfiguredChanged(bool value) => OnPropertyChanged(nameof(ApiKeyButtonText));

    /// <summary>页头入口按钮文案（状态化，未设置时明确告知而不是留空白按钮）。</summary>
    public string ApiKeyButtonText => ApiKeyConfigured ? "API Key 已设置" : "API Key 未设置";

    /// <summary>
    /// 保存或清除 API Key（由 View 在录入小窗返回后调用），随后**重载一次**以应用在线增强。
    /// <para>
    /// 🔴 VM **不弹窗**：小窗由 View 负责（审查纪律：VM 直弹对话框属反模式）。
    /// 传入 <c>null</c>/空白 = 清除。
    /// </para>
    /// </summary>
    /// <param name="apiKey">用户填写的 Key；空白表示清除。</param>
    public async Task ApplyApiKeyAsync(string? apiKey)
    {
        if (_apiKeyStore is null)
        {
            // 🟠 v11~v14 后续批次：原先直接 return —— 窗口已正常关闭（DialogResult=true），
            // 用户以为存下了、页头却仍显示"未设置"，且零日志零提示。组合根未注册
            // ISteamApiKeyStore 时"在线库存整体降级为本地"是**设计支持的路径**，故不算异常，
            // 但必须留痕 + 由 View 侧 CanStoreApiKey 提前拒绝（见 GameManagerView.OnApiKeyClick）。
            _logger.Warn("API Key 存储不可用（组合根未注册 ISteamApiKeyStore），保存请求被忽略");
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _apiKeyStore.Clear();
            _logger.Info("已清除 Steam Web API Key");
        }
        else
        {
            _apiKeyStore.Set(apiKey);
            // 🔴 日志只记"已保存"，绝不记 Key 本身（诊断导出会带走日志）
            _logger.Info("已保存 Steam Web API Key（DPAPI 加密落盘）");
        }

        ApiKeyConfigured = _apiKeyStore.Get() is not null;
        await LoadCommand.ExecuteAsync(null).ConfigureAwait(true); // 走命令 → 自动尊重 CanExecute（加载中不重入）
    }

    /// <summary>API Key 存储是否可用（组合根是否注册了 <c>ISteamApiKeyStore</c>）。
    /// View 侧据此在打开输入窗之前就拒绝，避免"窗口正常关闭但什么都没发生"。</summary>
    public bool CanStoreApiKey => _apiKeyStore is not null;

    /// <summary>当前激活账户的 SteamID64（在线库存在线请求按账号查；无则 null 走退化为本地）。</summary>
    private static string? ActiveSteamId64(SteamAllData data) =>
        (data.Users.FirstOrDefault(u => u.MostRecent) ?? data.Users.FirstOrDefault())?.SteamId64;

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

    /// <summary>
    /// 封面补全进度（底部状态栏第二行；空串 → 整行隐藏）。
    /// <para>
    /// 🟠 E-🟠-4（2026-09-15）：单轮上限 <see cref="MaxCoverFetchPerPass"/> 触顶时，
    /// "另有 N 张待下次刷新继续"此前**只写 FileLogger**，界面无任何出口 —— 用户看到大片字母占位
    /// 会以为封面全挂了。此处把待补数量暴露到界面。
    /// </para>
    /// <para>
    /// 与 <see cref="StatusText"/> **分离**成独立属性（不共享状态栏文案）—— 避免补全进度覆盖业务状态。
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverFetchProgress))]
    private string _coverFetchProgressText = string.Empty;

    /// <summary>是否有封面补全进度可显示（空串 = 无 → XAML 整行 Collapsed）。</summary>
    public bool HasCoverFetchProgress => !string.IsNullOrEmpty(CoverFetchProgressText);

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

    /// <summary>页头副标题（HTML 参考稿口径：「N 款游戏 · M 个库」）。N = **当前可见**数量，与网格所见一致。</summary>
    public string HeaderSubtitle => SteamInstalled
        ? $"{VisibleGameCount} 款游戏 · {LibraryCount} 个库"
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
                // 判据用 IsInstalled（有 .acf）而非 IsFullyInstalled：待更新的游戏**确实装着、也确实占盘**，
                // 用「装全」去数会让「N 款已安装」少算（2026-09-13 随 bit1 收紧一并改口径）。
                if (g.IsInstalled)
                {
                    installed++;
                }

                total += g.Model.SizeOnDisk;
            }

            return $"{installed} 款已安装 · 共占用 {GameVmFormat.SizeText(total)}";
        }
    }

    /// <summary>Games 集合或其派生统计变化后调用（刷新页头副标题、状态栏与空态投影）。</summary>
    private void NotifySummary()
    {
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(InstalledSummary));
        OnPropertyChanged(nameof(ShowNoResultEmpty));
        OnPropertyChanged(nameof(ShowEmptyLibrary));
        OnPropertyChanged(nameof(FilteredEmptyTitle));
        OnPropertyChanged(nameof(FilteredEmptyHint));
    }

    /// <summary>
    /// 过滤后为空（有数据、但可见集为 0）——空态层可见性投影。
    /// 判据用 <b>可见数</b>而非 <c>GamesView.IsEmpty</c>：隐藏未安装后可能整页为空，
    /// 那时必须给空态，否则用户看到的是**一片空白且没有任何解释**。
    /// </summary>
    public bool ShowNoResultEmpty => SteamInstalled && !IsLoading && Games.Count > 0 && VisibleGameCount == 0;

    /// <summary>库为空（Steam 已安装但**连未安装记录都没有**）——空态层可见性投影。</summary>
    public bool ShowEmptyLibrary => SteamInstalled && !IsLoading && Games.Count == 0;

    partial void OnSearchQueryChanged(string value) => ApplyFilter(); // 空态文案也依赖过滤结果

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

        // B2（2026-09-13）：未安装的游戏默认隐藏。与搜索词是「与」的关系——
        // 先判开关，再判关键词（两个条件互不掩盖，空态文案才能说清是哪一个挡住的）。
        if (!ShowNotInstalled && !vm.IsInstalled)
        {
            return false;
        }

        string q = SearchQuery.Trim();
        // 中文名与英文原名**都可命中**：显示用中文，但用户很可能按 Steam 商店里的英文名来找
        return q.Length == 0
            || vm.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || vm.NameOriginal.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // ================= B2 库存显示（2026-09-13） =================

    /// <summary>
    /// 是否显示未安装的游戏（默认 <c>false</c>：保持"只看已安装"的原有心智，避免一进页面就多出几百张卡）。
    /// </summary>
    [ObservableProperty]
    private bool _showNotInstalled;

    partial void OnShowNotInstalledChanged(bool value)
    {
        // 勾选变化必须重算可见集并刷新派生投影（集合本身没变，通知不会自己发）
        ApplyFilter();
        // 刚显示出来的未安装卡片此前未补过封面 → 这时才为它们发外呼
        StartCoverFetch();
    }

    /// <summary>被开关隐藏的未安装条目数（空态据此说清"为什么看不到"，而不是简单说"没有匹配"）。</summary>
    internal int HiddenNotInstalledCount
    {
        get
        {
            int count = 0;
            foreach (GameCardVm g in Games)
            {
                if (!g.IsInstalled)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>当前可见（过滤后）条目数。</summary>
    internal int VisibleGameCount => GamesView is CollectionView view ? view.Count : Games.Count;

    /// <summary>重算过滤并刷新所有派生投影（搜索词 / 「显示未安装」变化后调用）。</summary>
    internal void ApplyFilter()
    {
        GamesView.Refresh();
        NotifySummary();
    }

    /// <summary>是否因为「显示未安装」开关而看不到东西（用于空态给准确原因）。</summary>
    private bool HiddenByNotInstalledToggle =>
        !ShowNotInstalled && Games.Count > 0 && Games.Count == HiddenNotInstalledCount;

    /// <summary>筛选后为空态的标题（区分「搜索没匹配」与「未安装被隐藏」两种情况）。</summary>
    internal string FilteredEmptyTitle =>
        HiddenByNotInstalledToggle ? "未找到可显示的游戏" : "未找到匹配的游戏";

    /// <summary>
    /// 筛选后为空态的说明文案。
    /// 🔴 不许只说"换个关键词"——用户需要知道**究竟被什么挡住了**（搜索词 or 显示开关），
    /// 否则会以为游戏丢了。
    /// </summary>
    internal string FilteredEmptyHint
    {
        get
        {
            int hidden = HiddenNotInstalledCount;
            bool searching = SearchQuery.Trim().Length > 0;
            if (hidden == 0)
            {
                return "换个关键词试试，或清空搜索查看全部。";
            }

            return searching
                ? $"已隐藏 {hidden} 款未安装的游戏 —— 换个关键词，或勾选「显示未安装」查看"
                : $"已隐藏 {hidden} 款未安装的游戏 —— 勾选「显示未安装」后可查看";
        }
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
        CoverFetchProgressText = string.Empty;   // E-🟠-4：新一轮加载清掉上一轮补全进度
        try
        {
            SteamAllData data = await Task.Run(_steam.GetAllData).ConfigureAwait(true);

            // 批次 4（二）在线增强：**本地为底、在线可选**。未装 Steam / 未配置 Key → 原样返回、零外呼；
            // 失败 → 保留本地结果并回填 Error（页面如实标注来源，绝不把本地数据当完整库）。
            data = data with
            {
                Inventory = await _steam.EnhanceInventoryAsync(
                    data.Inventory, _apiKeyStore?.Get(), ActiveSteamId64(data)).ConfigureAwait(true),
            };

            SteamInstalled = data.InstallInfo.Installed;
            SteamRunning = data.InstallInfo.IsRunning;

            Games.Clear();
            if (SteamInstalled)
            {
                // 🔴 顺序关键：先落 Steam 安装目录，再建卡片——否则首屏封面探测拿不到主目录路径（实测踩坑）
                SteamInstallPath = data.InstallInfo.InstallPath;

                // 封面探测并行预计算（性能审查 P1-5）：每卡最多 9 次候选路径探测 × N 卡，
                // 原实现在 UI 线程逐卡同步探测，200 卡 = 上千次同步文件打开阻塞首屏
                // B2：建卡源 = 库存（已安装 ∪ 有游玩记录）。库存为空但已安装清单非空 → 退化为只有已安装
                IReadOnlyList<SteamInventoryGame> cardSource = ResolveCardSource(data);
                var installedOnly = cardSource.Where(g => g.Installed).ToList();

                string installPath = SteamInstallPath ?? string.Empty;
                (System.Collections.Concurrent.ConcurrentDictionary<uint, string> coverPaths, HashSet<string> offlineLibs) = await Task.Run(() =>
                {
                    var map = new System.Collections.Concurrent.ConcurrentDictionary<uint, string>();
                    // 封面与库离线态只对「已安装」项有意义：未安装条目没有库路径，本地也不会有封面
                    Parallel.ForEach(installedOnly, new ParallelOptions { MaxDegreeOfParallelism = 4 }, g =>
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
                    foreach (string lib in installedOnly.Select(g => g.LibraryPath).Distinct(StringComparer.OrdinalIgnoreCase))
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

                foreach (SteamInventoryGame g in cardSource)
                {
                    string cover = coverPaths.TryGetValue(g.AppId, out string? cp) ? cp : string.Empty;
                    // 未安装条目强制非离线：它的 LibraryPath 是空串，判定只会得出无意义的"离线"
                    bool offlineLib = g.Installed && offlineLibs.Contains(g.LibraryPath);
                    var vm = new GameCardVm(g, cover, offlineLib, this);
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

            // CDN 封面兜底：只为**当前可见**且缺封面的卡片补（见 StartCoverFetch 注释）
            StartCoverFetch();
            GamesView.Refresh();
            // 数据来源如实标注（批次 4）：在线成功/本地降级/未配置 Key 三种措辞互不混淆。
            // 在线降级不是"加载失败"，但必须让用户看见 → 状态栏转警示色（Level 1）。
            StatusLevel = data.Inventory.Error is null ? 0 : 1;
            StatusText = SteamInstalled
                ? BuildInventoryStatusText(data.Inventory, ApiKeyConfigured)
                : "未检测到 Steam 客户端";
            SteamInventoryStats stats = data.Inventory.Stats;
            _logger.Info(
                $"游戏库加载完成：库存 {stats.Total} 款（来源 {data.Inventory.Source}，"
                + $"已安装 {stats.Installed} / 未安装 {stats.NotInstalled}）"
                + $"，当前展示 {VisibleGameCount} 款，SteamInstalled={SteamInstalled}");
            if (data.Inventory.Error is not null)
            {
                _logger.Warn($"库存扫描存在错误：{data.Inventory.Error}");
            }
        }
        catch (Exception ex)
        {
            StatusText = "Steam 库读取失败：" + ex.Message;
            StatusLevel = 2;
            _logger.Error("Steam 库读取失败", ex);

            // 🟠 v11~v14 后续批次：SteamInstalled 默认 true（"加载完成前不闪空态"），而赋值
            // 只在 EnhanceInventoryAsync 成功之后 ⇒ 中途抛异常时它仍是 true ⇒ UI 走"游戏卡网格"
            // 分支并显示空态"未发现任何游戏记录"，而事实可能是**根本没装 Steam**（结论错）。
            // 未拿到安装信息且此前无任何数据时置 false，让 UI 如实显示"未检测到 Steam"。
            if (Games.Count == 0)
            {
                SteamInstalled = false;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanLoad => !IsLoading;

    /// <summary>
    /// 取用于建卡的库存条目。
    /// <para>
    /// 🔴 降级不丢数据：库存为空但已安装清单非空（库存扫描失败）时**退化为"只有已安装"**，
    /// 而不是让整页变空——少一个数据源，不等于该把已经装好的东西也藏起来。
    /// </para>
    /// </summary>
    private IReadOnlyList<SteamInventoryGame> ResolveCardSource(SteamAllData data)
    {
        if (data.Inventory.Games.Count > 0)
        {
            return data.Inventory.Games;
        }

        if (data.Games.Count > 0)
        {
            _logger.Warn("库存为空 —— 退化为主显已安装清单（本轮未安装的游戏不可见）");
        }

        return data.Games.Select(g => new SteamInventoryGame
        {
            AppId = g.AppId,
            Name = g.Name,
            Installed = true,
            PlaytimeMinutes = g.PlaytimeMinutes,
            LastPlayed = g.LastPlayed,
            SizeOnDisk = g.SizeOnDisk,
            StateFlags = g.StateFlags,
            InstallDir = g.InstallDir,
            LibraryPath = g.LibraryPath,
        }).ToList();
    }

    /// <summary>
    /// 状态栏文案：**如实交代来源 + 还有多少未安装的游戏**。
    /// 只报「找到 N 款」会让用户以为库里就这些——那是最容易被忽略的状态欺骗；
    /// 同理，在线失败时必须写明"本地缓存"与原因，否则用户会以为看到的是完整库存（批次 4）。
    /// </summary>
    /// <param name="inventory">库存快照（含来源与降级原因）。</param>
    /// <param name="apiKeyConfigured">是否已配置 API Key（未配置时给中性提示，不是错误）。</param>
    private static string BuildInventoryStatusText(
        SteamInventorySnapshot inventory,
        bool apiKeyConfigured)
    {
        SteamInventoryStats stats = inventory.Stats;
        bool online = inventory.Source == SteamInventorySource.Online;
        string origin = online ? "在线" : "本地缓存";
        string head = stats.NotInstalled > 0
            ? $"库存 {stats.Total} 款（{origin}）：已安装 {stats.Installed}"
                + $" · 另有 {stats.NotInstalled} 款未安装（勾选「显示未安装」查看）"
            : $"库存 {stats.Total} 款（{origin}）：全部已安装";

        string note = inventory.Error is not null
            ? $" · 在线数据不可用：{inventory.Error}"
            : (online || apiKeyConfigured) ? string.Empty : " · 未设置 API Key（仅显示本机数据）";

        // 「库 N 个」不在这里出现：页头副标题（HeaderSubtitle）已给，两处重复是 2026-09-13 UI 评审指出的问题
        return $"{head}{note}";
    }

    /// <summary>
    /// 单轮封面补全上限（批次 4 在线扩容后的保护）。在线库存可能带来上百条无封面条目，
    /// 勾选「显示未安装」后全量外呼 = 上百次 CDN 请求（每张 10s 超时、并发 3）。
    /// <para>
    /// 🔴 只对"在线扩充后"生效：纯本地场景最多三四十条，永远碰不到 → 既有行为不变。
    /// 剩余部分由用户**下次刷新**继续补（不做自动续轮——那等于变相取消上限）。
    /// </para>
    /// </summary>
    private const int MaxCoverFetchPerPass = 40;

    /// <summary>
    /// 为「当前可见且缺封面」的卡片后台补封面（限并发 3、10s/张、失败静默保留占位）。
    /// <para>
    /// 🔴 待补清单**必须在本方法（调用线程 = UI 线程）物化**再交给线程池：把 UI 绑定的
    /// <c>ObservableCollection</c> 的枚举整体挪进后台线程 = 把"UI 冻结"换成"跨线程竞态"。
    /// </para>
    /// <para>
    /// 只取**可见**项：勾选关闭时不为几十上百款不显示的未安装游戏发外呼；
    /// 用户勾选「显示未安装」时由 <see cref="OnShowNotInstalledChanged"/> 再触发一次。
    /// 单轮数量另受 <see cref="MaxCoverFetchPerPass"/> 约束。
    /// </para>
    /// </summary>
    private void StartCoverFetch()
    {
        var candidates = Games
            .Where(g => !g.HasCover && (g.IsInstalled || ShowNotInstalled))
            .ToList();
        var coverless = candidates.Take(MaxCoverFetchPerPass).ToList();
        int deferred = candidates.Count - coverless.Count;
        if (coverless.Count == 0)
        {
            CoverFetchProgressText = string.Empty;   // 无待补项 → 清空（E-🟠-4）
            return;
        }

        // 🟠 E-🟠-4：待补数量除 FileLogger 外**同时**暴露到状态栏第二行 —— 上限触顶时
        // 用户必须能看出"还有 N 张、下次刷新继续"，否则大片字母占位会被读成"封面全挂了"。
        CoverFetchProgressText = deferred > 0
            ? $"封面补全中：本轮 {coverless.Count} 张，另有 {deferred} 张待下次刷新继续（单轮上限 {MaxCoverFetchPerPass}）"
            : $"封面补全中：本轮 {coverless.Count} 张";

        if (deferred > 0)
        {
            _logger.Info(
                $"封面补全：本轮处理 {coverless.Count} 张，另有 {deferred} 张待下次刷新继续"
                + $"（单轮上限 {MaxCoverFetchPerPass}）");
        }

        string cacheDir = Path.Combine(
            System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "cache", "steam-covers");
        // REVIEW-3 A-2：串行逐张（每张 10s 超时）在缺封面多时补全过慢（50 张 ≈ 8 分钟），改限并发 3
        _ = Task.Run(async () =>
        {
            // 🟠 V13-G2（2026-09-14 审查）：fire-and-forget 任务**整体**兜底。原先 lambda 体无外层 catch，
            // gate.Dispose() 与收尾 _logger.Warn 都裸露在 try 之外——此处任一步抛异常只会走到
            // App.xaml.cs 的 TaskScheduler.UnobservedTaskException：该事件**只在 Task 被 GC 终结时触发**
            // （时点不确定，可能直到进程退出都不触发）且无 AppLog/UI 出口（核实记录 v13 §二 🟠-2）。
            try
            {
                var gate = new System.Threading.SemaphoreSlim(3);
                // 审查 🟠-6 采纳：单张静默改为计数 + 收尾汇总一条日志（逐张记会在断网时刷上百条）
                int failed = 0;
                try
                {
                    await Task.WhenAll(coverless.Select(async vm =>
                    {
                        try
                        {
                            await gate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                string? path = await SteamService.EnsureCoverFromCdnAsync(
                                        cacheDir, vm.AppId, default, vm.Model.HeaderImage)
                                    .ConfigureAwait(false);
                                if (path is not null)
                                {
                                    RunOnUi(() => vm.SetCover(path)); // 审查 🔴-2：后台线程 PropertyChanged 统一编组
                                }
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Threading.Interlocked.Increment(ref failed);
                            // v11~v14 后续批次：原为 Debug.WriteLine（Release 下被编译器剪裁 ⇒ 异常零留痕）；
                            // 改用 {ex} 而非 {ex.Message} —— ILogger.Warn 无异常重载，ToString() 才带类型与堆栈。
                            _logger.Warn($"[游戏] CDN 封面补全失败（AppId {vm.AppId}）：{ex}");
                        }
                    })).ConfigureAwait(false);
                }
                finally
                {
                    gate.Dispose();
                }

                if (failed > 0)
                {
                    _logger.Warn($"[游戏] CDN 封面补全失败 {failed}/{coverless.Count} 张（无网或超时），已保留占位图");
                }

                // E-🟠-4：本轮结束落"完成态"（后台线程 → 必须经 RunOnUi 编组回 UI 线程）
                RunOnUi(() =>
                {
                    string failedTail = failed > 0 ? $"，{failed} 张失败（已保留占位图）" : "";
                    CoverFetchProgressText = deferred > 0
                        ? $"封面补全完成：本轮 {coverless.Count} 张{failedTail}，另有 {deferred} 张待下次刷新继续"
                        : $"封面补全完成：{coverless.Count} 张{failedTail}";
                });
            }
            catch (Exception ex)
            {
                // 兜底只保证"不静默"：⚠️ ILogger.Warn 无异常重载（仅 Warn(string)），故取 Message；
                // 与同文件既有「封面探测失败…」的 Warn 写法一致。
                _logger.Warn($"[游戏] CDN 封面补全异常：{ex.Message}");
                // E-🟠-4：异常也要如实收口，不能让第二行永远停在"补全中…"
                RunOnUi(() => CoverFetchProgressText = "封面补全异常中断（详见日志）");
            }
        });
    }

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
                // 🟡 V13-G7（2026-09-14）：`IsClientRunning()` 内部是
                // `Process.GetProcessesByName("steam")`（枚举全进程并物化 Process 对象）——
                // 同步跑在 UI 线程上会卡住界面，故与上文 SwitchAccount 同款下移后台。
                SteamRunning = await Task.Run(() => _steam.IsClientRunning()).ConfigureAwait(true);
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

    // ================= A2 详情面板（2026-09-13） =================

    /// <summary>详情面板当前查看的游戏；<c>null</c> = 面板关闭（View 用它驱动遮罩与面板可见性）。</summary>
    [ObservableProperty]
    private GameCardVm? _selectedGame;

    partial void OnSelectedGameChanged(GameCardVm? value) => OnPropertyChanged(nameof(IsDetailOpen));

    /// <summary>详情面板是否打开。</summary>
    public bool IsDetailOpen => SelectedGame is not null;

    /// <summary>打开详情面板（卡片封面/名称单击触发；参数 = 该卡 GameCardVm）。</summary>
    [RelayCommand]
    private void OpenDetail(GameCardVm? vm) => SelectedGame = vm;

    /// <summary>关闭详情面板（✕ 按钮 / 点遮罩 / Esc 三个入口）。</summary>
    [RelayCommand]
    private void CloseDetail() => SelectedGame = null;

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

    // 🔴 卸载入口已整体移除（2026-09-13 主人裁定）：卸载一律交给 Steam 客户端自己做，
    //    本软件不再提供卸载按钮/菜单项，故 UninstallGameCommand 与其确认门一并删除。
    //    Core 的 SteamService.UninstallGame（steam://uninstall 协议封装）**保留**——它本身就是
    //    「交给 Steam 客户端」的机制、且设计 §6 记载该能力；当前无 UI 调用点。

    /// <summary>
    /// 安装引导（批次 4 · B3）：<c>steam://install</c> 协议拉起 Steam 安装向导。
    /// <para>
    /// 与 <see cref="UninstallGame"/> 同构。**不加本程序的确认门**——安装非破坏性，
    /// 而 Steam 自己的向导里还要选盘、选语言、再确认一次；在这里再弹一次纯属重复打断。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void InstallGame(GameCardVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        Guard($"安装《{vm.Name}》", () =>
        {
            bool ok = _steam.InstallGame(vm.AppId);
            StatusText = ok ? $"已提交安装请求：{vm.Name}（请在 Steam 窗口中确认）" : $"安装请求失败：{vm.Name}";
            StatusLevel = ok ? 1 : 2;
            _logger.Info($"安装游戏 {vm.Name}：{ok}");
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
