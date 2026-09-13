using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Modules.FileTransfer;

/// <summary>局域网已发现设备行投影。</summary>
public partial class DiscoveredDeviceRowVm : ObservableObject
{
    public DiscoveredDevice Model { get; }

    public DiscoveredDeviceRowVm(DiscoveredDevice model) => Model = model;

    public string Name => Model.Name;

    public string EndpointText => $"{Model.IPAddress}:{Model.TransferPort}";

    public string OnlineText => Model.IsOnline ? "在线" : "离线";

    /// <summary>
    /// 是否已在「已知设备」列表里（2026-09-13 批次 P3 ⑭）。由 VM 在设备列表刷新与
    /// 已知列表增删后统一重算——判断"是否已加入"的依据只能有一处。
    /// </summary>
    [ObservableProperty]
    private bool _isKnown;
}

/// <summary>已知设备（记忆的常用对端）行 VM，可编辑并持久化。</summary>
public partial class KnownPeerRowVm : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _ip = "";

    /// <summary>端口（**持久化用的真值**；只在文本合法时被更新）。</summary>
    [ObservableProperty]
    private int _port = 18889;

    /// <summary>
    /// 端口输入（字符串承载）。
    /// <para>
    /// 🔴 为什么不用 int 直绑（FT-11，2026-09-13 修）：WPF 的字符串→int 转换失败是**静默**的——
    /// 用户清空该框或输入「1a」时，界面显示为空、而绑定源仍是旧值，用户会以为改成功了。
    /// 改为字符串承载 + 就地校验：非法值给出可见错误，只有合法值才写进 <see cref="Port"/>。
    /// （与桌面/手机两页端口框、以及 `PeerEditBox` 的既有范式一致。）
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _portText = "18889";

    /// <summary>端口校验错误（空串 = 无错；XAML 据此收起整行提示）。</summary>
    [ObservableProperty]
    private string _portError = "";

    partial void OnPortChanged(int value) => PortText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    partial void OnPortTextChanged(string value)
    {
        if (int.TryParse(value, out int parsed) && PortValidator.IsInRange(parsed))
        {
            Port = parsed;
            PortError = string.Empty;
        }
        else
        {
            // 不阻断输入，也不静默丢弃：如实说明当前不会生效，以及发送时用的是哪个端口
            PortError = "端口需为 1024–65535 的整数；未填合法值前，「发送文件」仍用上一次的有效端口。";
        }
    }
}

/// <summary>传输任务行投影：定时刷新速度/进度投影（TransferTask 为普通 CLR 属性）。</summary>
public sealed class TransferTaskRowVm : ObservableObject
{
    public TransferTask Model { get; }

    public TransferTaskRowVm(TransferTask model) => Model = model;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(ReasonText));
        // 状态派生的一切都要重算：暂停/恢复会让按钮文案与可用性同时翻转，
        // 漏一条就会出现"状态已变成已暂停，按钮还写着暂停"
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(PauseResumeText));
    }

    public string FileName => Model.FileName;

    public string PeerEndpoint => Model.PeerEndpoint;

    public double Progress => Model.Progress;

    public string SpeedText => Model.EstimatedRemaining is null
        ? Model.SpeedText
        : $"{Model.SpeedText} · 剩余 {Model.EstimatedRemaining.Value:hh\\:mm\\:ss}";

    public string StatusText => Model.Status switch
    {
        TransferStatus.Pending => "排队中",
        TransferStatus.Negotiating => "等待确认",
        TransferStatus.Transferring => "传输中",
        TransferStatus.Paused => Model.PausedByPeer ? "已暂停（对端）" : "已暂停",
        TransferStatus.Completed => "已完成",
        TransferStatus.Skipped => "已跳过",
        TransferStatus.Failed => "失败",
        TransferStatus.Cancelled => "已取消",
        _ => Model.Status.ToString(),
    };

    /// <summary>
    /// 原因文案：优先机器可读原因码（稳定判据），没有码时才退回散文。
    /// 历史与任务行共用这一条规则，避免两处措辞各写一套。
    /// </summary>
    public string ReasonText => string.IsNullOrEmpty(Model.ReasonCode)
        ? Model.ErrorMessage ?? string.Empty
        : TransferReasonCodes.Describe(Model.ReasonCode);

    public string DirectionText => Model.Direction == TransferDirection.Send ? "↑ 发送" : "↓ 接收";

    /// <summary>
    /// 是否仍占着资源（进度/取消/暂停都据此显示）。
    /// 🔴 必须含 <see cref="TransferStatus.Paused"/>：暂停中的任务照样占着接收并发槽与 .part，
    /// 若把它判成"非活跃"，用户就会看到任务行突然失去取消键——而它还在后台占着资源。
    /// </summary>
    public bool IsActive => Model.Status is TransferStatus.Pending or TransferStatus.Negotiating
        or TransferStatus.Transferring or TransferStatus.Paused;

    /// <summary>是否可暂停（只有还在跑的任务能暂停）。</summary>
    public bool CanPause => Model.Status is TransferStatus.Pending or TransferStatus.Negotiating
        or TransferStatus.Transferring;

    /// <summary>是否可恢复。</summary>
    public bool CanResume => Model.Status == TransferStatus.Paused;

    /// <summary>暂停/恢复按钮的文案（同一按钮位轮流承担两个动作，不额外占列宽）。</summary>
    public string PauseResumeText => CanResume ? "继续" : "暂停";
}

/// <summary>
/// 「电脑互传」Tab：传输服务启停、设备发现、已知设备、发送（多选/拖拽）、
/// 活跃任务列表（确认门弹窗 / 取消）、传输历史。配置持久化于
/// %LOCALAPPDATA%\SystemToolkit\net\filetransfer.json（原子写）。
/// </summary>
public partial class FileTransferDesktopViewModel : ObservableObject
{
    private static readonly string ConfigPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "net", "filetransfer.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly IDeviceDiscoveryService _discovery;
    private readonly FileTransferService _transfer;
    private readonly TransferHistoryService _history;
    private readonly Action<string> _log;
    private readonly ILogger _logger;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;

    public FileTransferDesktopViewModel(
        IDeviceDiscoveryService discovery,
        FileTransferService transfer,
        TransferHistoryService history,
        Action<string> log,
        ILogger logger,
        System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _discovery = discovery;
        _transfer = transfer;
        _history = history;
        _log = log;
        _logger = logger;
        _dispatcher = dispatcher;

        _transfer.TaskUpdated += OnTaskUpdated;
        _transfer.TaskCompleted += OnTaskCompleted;
        _transfer.TransferRequested += OnTransferRequested;
        _discovery.DeviceChanged += OnDeviceChanged;

        // 历史筛选视图（P3 ⑮）：建在构造里、绑到 XAML 的 Desktop.HistoryView，
        // 这样"筛选条件"只有一处真相（VM），XAML 不参与过滤逻辑。
        HistoryView = System.Windows.Data.CollectionViewSource.GetDefaultView(HistoryEntries);
        HistoryView.Filter = FilterHistory;
    }

    /// <summary>
    /// 后台事件 → UI 线程编组（审查 🔴-1 采纳：替换 Application.Current?.Dispatcher.Invoke——
    /// 同步 Invoke 有死锁风险、Application 为 null 时静默跳过；与 MusicManager 统一为
    /// 「显式 Dispatcher 注入 + 死线程检测 + BeginInvoke」模式）。测试传 null 直执行。
    /// </summary>
    private void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? d = _dispatcher;
        if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive)
        {
            RunGuarded(action);
        }
        else if (d.CheckAccess())
        {
            RunGuarded(action);
        }
        else
        {
            d.BeginInvoke(() => RunGuarded(action));
        }
    }

    /// <summary>处理器异常不得反噬服务回调线程：落可见日志（🔴 不静默）。</summary>
    private void RunGuarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log($"[互传] ⚠️ 界面更新异常：{ex.Message}");
            _logger.Error("[互传] UI 事件处理器异常", ex);
        }
    }

    /// <summary>确认对话框回调（由组合根转接；用于"清空历史"这类纯确认）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    /// <summary>
    /// 接收确认回调（View 注入；返回用户的决定，含同名处理方式）。
    /// <para>
    /// 与 <see cref="ConfirmRequest"/> 分开的原因：接收确认要展示"存到哪/盘剩多少/同名会怎样"
    /// 并允许逐次选择处理方式，两个 bool 表达不了（用 bool 就得让 UI 去改全局策略 = 越权）。
    /// </para>
    /// </summary>
    public Func<TransferRequestEventArgs, TransferDecision>? ConfirmTransferRequest { get; set; }

    /// <summary>
    /// 把一段文本写入本机剪贴板（View 注入；返回是否成功）。
    /// <para>
    /// 为什么由 View 注入而不是 VM 直接调 <c>Clipboard</c>：与 <see cref="ConfirmRequest"/> /
    /// <see cref="ConfirmTransferRequest"/> 同款约定（VM 不碰 UI 资源）；注入后还可被替换成
    /// "必定失败"以验证诚实性判据。
    /// </para>
    /// <para>
    /// ⚠️ 收到**文本**时它必须可用：没有它就没有交付。此时按**写入失败**处理（而非默认成功）
    /// —— 缺能力时宁可如实报失败，也不假报送达。
    /// </para>
    /// </summary>
    public Func<string, bool>? WriteClipboard { get; set; }

    /// <summary>
    /// 读取本机剪贴板文本（View 注入；失败或为空返回 null）。
    /// <para>
    /// 与 <see cref="WriteClipboard"/> 同款约定：UI 资源不进 VM，注入后也可被单测替换。
    /// 读失败（剪贴板被占用 / 内容非文本）是**预期内**路径 —— 就地提示，不弹错误框。
    /// </para>
    /// </summary>
    public Func<string?>? ReadClipboard { get; set; }

    // ── 文本 / 剪贴板发送块（FT-3 / B8b-2） ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextSizeText))]
    [NotifyPropertyChangedFor(nameof(TextSendHintText))]
    [NotifyCanExecuteChangedFor(nameof(SendTextCommand))]
    private string _textToSend = string.Empty;

    /// <summary>当前文本的 UTF-8 体积与上限（如 <c>412 B / 256 KB</c>）。与协议限长同一口径。</summary>
    public string TextSizeText
        => $"{TransferText.GetByteCount(TextToSend):N0} B / {TransferText.MaxBytes / 1024} KB";

    /// <summary>
    /// 发送按钮是否可用：**只看文本本身**（非空且未超上限）。
    /// <para>
    /// 刻意不管"有没有选目标"：选中态变化不会触发 CanExecute 重新求值，
    /// 把目标也纳入条件会让按钮在该亮的时候不亮（既有的发送文件按钮同样只在选择缺失时就地提示）。
    /// </para>
    /// </summary>
    public bool CanSendText => TransferText.Validate(TextToSend).IsValid;

    /// <summary>
    /// 文本块下方的就地说明。🔴 必须有它：本主题的**禁用态几乎不可见**，
    /// 只靠按钮变灰用户根本看不出为什么点不动（既有评审结论）。
    /// </summary>
    public string TextSendHintText
    {
        get
        {
            TextValidation validation = TransferText.Validate(TextToSend);
            if (validation.IsValid)
            {
                return string.Empty;
            }
            return string.IsNullOrWhiteSpace(TextToSend)
                ? $"输入或粘贴要发送的文本；上限 {TransferText.MaxBytes / 1024} KB（UTF-8 字节）"
                : validation.ErrorText;
        }
    }

    /// <summary>文件选择对话框回调（View 注入；返回 null 表示用户取消）。</summary>
    public Func<IReadOnlyList<string>?>? PickFiles { get; set; }

    /// <summary>当前发送目标（设备行 / 已知设备行二选一，发送时由 View 落定）。</summary>
    public (string Ip, int Port)? PendingSendTarget { get; set; }

    // ── 服务与配置 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortEffectHint))]
    private bool _isTransferRunning;

    [ObservableProperty]
    private string _deviceId = "";

    [ObservableProperty]
    private string _receiveDirectory = "";

    /// <summary>
    /// 接收目录所在盘剩余空间文案（2026-09-13 批次 P3 ⑯）。与接收前磁盘预检同一数据源
    /// （<c>DiskSpaceUtil</c>）——用户看到的数字与预检的判据必须是同一份事实。
    /// </summary>
    [ObservableProperty]
    private string _receiveDirectoryFreeText = "";

    partial void OnReceiveDirectoryChanged(string value) => RefreshReceiveDirectoryFreeSpace();

    private void RefreshReceiveDirectoryFreeSpace()
    {
        string dir = ReceiveDirectory?.Trim() ?? string.Empty;
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            // 目录还没建（服务未启动）时如实留空，不猜、不显示 0
            ReceiveDirectoryFreeText = string.Empty;
            return;
        }

        long? free = DiskSpaceUtil.TryGetAvailableFreeBytes(dir);
        ReceiveDirectoryFreeText = free is null
            ? "剩余空间无法判定（网络路径或卷未就绪）"
            : $"剩余 {FormatUtil.FormatSize(free.Value)}";
    }

    // ── 端口（2026-09-13 批次 P3 ⑱） ──

    // 🔴 用**字符串**承载输入而不是直接绑 int：WPF 把 "" / "18" 这样的中间态转 int 会失败，
    // 失败是静默的（用户看到自己打的字被"吞"），且异常只在 Output 窗口可见。
    [ObservableProperty]
    private string _tcpPortText = "18889";

    [ObservableProperty]
    private string _discoveryPortText = "18888";

    /// <summary>端口校验错误文案（空串 = 无错）。</summary>
    [ObservableProperty]
    private string _portErrorText = "";

    /// <summary>校验通过的 TCP 端口（非法时为 0；调用方必须先看 <see cref="PortErrorText"/>）。</summary>
    public int TcpPort => int.TryParse(TcpPortText?.Trim(), out int port) ? port : 0;

    /// <summary>校验通过的发现端口（非法时为 0）。</summary>
    public int DiscoveryPort => int.TryParse(DiscoveryPortText?.Trim(), out int port) ? port : 0;

    partial void OnTcpPortTextChanged(string value) => SaveAndValidatePorts();

    partial void OnDiscoveryPortTextChanged(string value) => SaveAndValidatePorts();

    /// <summary>
    /// 端口生效时机提示。
    /// <para>
    /// 🔴 2026-09-13 实机反馈修正：原文案恒为「已改，重启服务后生效」，而判据其实是"服务是否在运行"
    /// —— 用户**根本没改过任何端口**也会看到"已改"。文案只能描述**当前状态 + 下一步动作**，
    /// 不能替用户断言他做了什么。
    /// </para>
    /// </summary>
    public string PortEffectHint => IsTransferRunning
        ? "服务运行中：改端口后需先「停止服务」再「启动服务」才生效"
        : "启动服务时生效";

    private void SaveAndValidatePorts()
    {
        PortErrorText = PortValidator.Validate(
            ("TCP 端口", TcpPort), ("UDP 端口", DiscoveryPort)) ?? string.Empty;
        OnPropertyChanged(nameof(TcpPort));
        OnPropertyChanged(nameof(DiscoveryPort));
        if (string.IsNullOrEmpty(PortErrorText))
        {
            _ = TryPersistConfig(out _); // 只落盘合法值，免得手滑时把坏值写进配置
        }
    }

    [ObservableProperty]
    private bool _requireKnownPeer = true;

    [ObservableProperty]
    private bool _requireReceiveConfirmation = true;

    /// <summary>
    /// 同名文件冲突策略（协议 §4.4；**两条接收通道共用**——电脑互传与手机上传都按它落定）。
    /// </summary>
    [ObservableProperty]
    private TransferConflictPolicy _conflictPolicy = TransferConflictPolicy.Rename;

    /// <summary>下拉选项（含中文标签；直接绑 enum 会显示英文枚举名）。</summary>
    public IReadOnlyList<ConflictPolicyOption> ConflictPolicyOptions { get; } =
    [
        new(TransferConflictPolicy.Rename, "自动改名（推荐）"),
        new(TransferConflictPolicy.Ask, "每次询问"),
        new(TransferConflictPolicy.Skip, "跳过不接收"),
        new(TransferConflictPolicy.Overwrite, "覆盖原文件（危险）"),
    ];

    /// <summary>策略变更即热切换（照 <c>RequireKnownPeer</c> 的既有范式）；未启动服务时只改设置。</summary>
    partial void OnConflictPolicyChanged(TransferConflictPolicy value)
    {
        if (IsTransferRunning)
        {
            _transfer.SetConflictPolicy(value);
        }
        _log($"[互传] 同名冲突策略：{TransferConflictResolver.Describe(value)}");
    }

    /// <summary>
    /// 暂停超时（分钟；0 = 不限）。默认 30。
    /// <para>
    /// 为什么必须有上限：暂停是单方面意图，对端无法区分「对面在暂停」与「对面挂了」——
    /// 没有上限，一次忘掉的暂停会让对端永久挂着、本机接收并发槽也一直被占。
    /// 目前只从模块配置文件读（界面未加输入框），改它需要编辑
    /// <c>%LOCALAPPDATA%\SystemToolkit\net\filetransfer.json</c> 的 <c>PauseTimeoutMinutes</c>。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private int _pauseTimeoutMinutes = 30;

    /// <summary>确认门等待时长（秒）。**单一来源**：既传给传输服务，也用于对话框上的倒计时提示。</summary>
    public int ReceiveConfirmTimeoutSeconds { get; } = 30;

    public string TransferPortText => _transferPort > 0 ? _transferPort.ToString() : "—";

    [ObservableProperty]
    private bool _initialized;

    private int _transferPort;

    // ── 设备 ──
    public ObservableCollection<DiscoveredDeviceRowVm> DiscoveredDevices { get; } = new();

    [ObservableProperty]
    private DiscoveredDeviceRowVm? _selectedDevice;

    /// <summary>
    /// 最近一次刷新设备列表的时间。
    /// <para>
    /// 🔴 2026-09-13 评审提「刷新缺加载反馈」——**实测本模块没有"扫描"这个操作**：
    /// <see cref="SystemToolkit.Core.FileTransfer.Services.DeviceDiscoveryService"/> 是 UDP 被动心跳
    /// （每 3 秒广播 + 监听），「刷新」只是把内存快照重列一遍、**瞬时完成**。给它加 spinner
    /// 等于造假（要么一闪而过、要么长时间空转）。故改为如实给出「最近刷新时间」+ 空态说明监听机制。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _lastDeviceRefreshText = "尚未刷新";

    public ObservableCollection<KnownPeerRowVm> KnownPeers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedKnownPeer))]
    private KnownPeerRowVm? _selectedKnownPeer;

    /// <summary>
    /// 是否选中了已知设备。编辑行（名称/IP/端口）**只在选中时出现**——
    /// 空态旁边常驻一排空输入框，用户既不知道该不该填、也不知道每格填什么
    /// （2026-09-13 实机反馈）。
    /// </summary>
    public bool HasSelectedKnownPeer => SelectedKnownPeer is not null;

    // ── 任务 ──
    public ObservableCollection<TransferTaskRowVm> ActiveTasks { get; } = new();

    [ObservableProperty]
    private TransferTaskRowVm? _selectedTask;

    // ── 历史 ──
    public ObservableCollection<TransferHistoryEntry> HistoryEntries { get; } = new();

    private bool _busy;

    /// <summary>页面 Loaded：读配置、注册事件（幂等）。</summary>
    public void Initialize()
    {
        if (Initialized)
        {
            return;
        }

        Initialized = true;
        LoadConfig();
        ReloadHistory();
        RefreshReceiveDirectoryFreeSpace(); // 目录剩余空间（P3 ⑯）：进页面就给，不必等服务启动
        _log($"[互传] 本机设备 ID：{_discovery.LocalDeviceId}");
    }

    // ── 服务启停 ──

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task StartTransferAsync()
    {
        _busy = true;
        RefreshToggleCanExecute();
        try
        {
            if (!string.IsNullOrEmpty(PortErrorText))
            {
                _log($"[互传] ⚠️ 端口设置非法，未启动：{PortErrorText}");
                return;
            }

            var settings = new TransferSettings
            {
                TransferPort = TcpPort,
                ReceiveDirectory = ReceiveDirectory,
                RequireKnownPeer = RequireKnownPeer,
                RequireReceiveConfirmation = RequireReceiveConfirmation,
                // 同名策略与暂停上限必须随启动一起传：漏传会让界面上的选择"看起来生效了"
                // 而实际仍走默认值（2026-09-13 用例先红抓住过同类缺陷）
                ConflictPolicy = ConflictPolicy,
                PauseTimeoutMinutes = PauseTimeoutMinutes,
                // 与确认对话框显示给用户的秒数同源：两处各写一个 30，界面承诺的时间就会是假的
                ReceiveConfirmTimeoutSeconds = ReceiveConfirmTimeoutSeconds,
            };
            // 审查 🟠-3 采纳（2026-09-09）：两步启动分别标注阶段——部分失败时用户能看出
            // 是传输服务还是设备发现服务没起来（原实现只有一条笼统异常）
            try
            {
                await _transfer.StartAsync(settings).ConfigureAwait(true);
            }
            catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
            {
                _log("[互传] ⚠ 操作已取消或超时。");
            }
            catch (Exception ex)
            {
                _log($"[互传] ❌ 传输服务启动失败（TCP {TcpPort} 可能被占用）：{ex.Message}");
                _logger.Error("传输服务启动失败", ex);
                throw;
            }

            _transferPort = TcpPort;
            OnPropertyChanged(nameof(TransferPortText));
            try
            {
                var discoverySettings = new TransferSettings
                {
                    DiscoveryPort = DiscoveryPort,
                    TransferPort = TcpPort,
                    HeartbeatInterval = TimeSpan.FromSeconds(3),
                    OfflineTimeout = TimeSpan.FromSeconds(10),
                };
                await _discovery.StartAsync(discoverySettings).ConfigureAwait(true);
            }
            catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
            {
                _log("[互传] ⚠ 操作已取消或超时。");
            }
            catch (Exception ex)
            {
                _log($"[互传] ❌ 设备发现服务启动失败（UDP {DiscoveryPort} 可能被占用）：{ex.Message}");
                _logger.Error("设备发现服务启动失败", ex);

                // 传输服务已起来但发现服务没起来：不回滚会留下"停止按钮不可用、服务却在跑"的僵局
                // （审查 🟠-3 的状态不一致）——这里做清理属于防御，不是新功能
                try
                {
                    await _transfer.StopAsync().ConfigureAwait(true);
                    _log("[互传] 已回滚停止传输服务（设备发现启动失败）");
                }
                catch (Exception stopEx)
                {
                    _logger.Warn($"[互传] 回滚停止传输服务失败：{stopEx.Message}");
                }

                throw;
            }

            IsTransferRunning = true;
            RefreshReceiveDirectoryFreeSpace(); // 目录此刻已被服务创建，剩余空间才有得算
            _log($"[互传] ✅ 传输与设备发现服务已启动（TCP {TcpPort} / UDP {DiscoveryPort}）");
            _logger.Info("互传服务启动");
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            _log("[互传] ⚠ 操作已取消或超时。");
        }
        catch (Exception ex)
        {
            _log("[互传] ❌ 服务启动失败：" + ex.Message + "（端口被占用？）");
            _logger.Error("互传服务启动失败", ex);
        }
        finally
        {
            _busy = false;
            RefreshToggleCanExecute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task StopTransferAsync()
    {
        _busy = true;
        RefreshToggleCanExecute();
        try
        {
            await _discovery.StopAsync().ConfigureAwait(true);
            await _transfer.StopAsync().ConfigureAwait(true);
            DiscoveredDevices.Clear();
            ActiveTasks.Clear();

            // 审查 F-5 采纳（2026-09-09）：停止时显式停表并置 null——
            // 原实现靠 Tick 内 All(!IsActive) 自停（空集合确实会停），但路径隐式；
            // 显式清理让"服务停了、定时器也一定停"成为不变量，避免残留 Tick 触碰已清空集合
            if (_taskTimer is { } timer)
            {
                timer.Stop();
                _taskTimer = null;
            }

            IsTransferRunning = false;
            _log("[互传] 服务已停止");

            // 审查 v5（🟡-9）：StopAsync 仅给在途任务 50ms 收尾宽限，其 TaskUpdated 经 RunOnUi
            // 排队后可能在上面 Clear() 之后才执行 Insert——延迟一拍再复清一次，
            // 防止已停止任务的"幽灵行"残留并复活刚停掉的定时器。
            // 审查 v7（A-3）：只移除非活动行、不清整表——300ms 内用户重启服务并入队的
            // 新任务行不能被连带删掉（谓词 Any(!IsActive) + Clear() 会误伤）。
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    RunOnUi(() =>
                    {
                        if (!IsTransferRunning)
                        {
                            foreach (TransferTaskRowVm row in ActiveTasks.Where(r => !r.IsActive).ToList())
                            {
                                ActiveTasks.Remove(row);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error("[互传] 停止后延迟复清失败", ex);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // v6 O-1c：取消不伪装为业务失败
            _log("[互传] ⚠ 停止操作已取消。");
        }
        catch (Exception ex)
        {
            // v6 O-1c：命令体真实兜底——原 try/finally 让异常被 AsyncRelayCommand 吞掉，
            // IsTransferRunning 停在 true 且用户零反馈（嵌套 lambda 的 catch 曾骗过守卫判据）
            _log("[互传] ❌ 停止失败：" + ex.Message + "（服务可能仍在运行，可重试停止）");
            _logger.Error("互传服务停止失败", ex);
        }
        finally
        {
            _busy = false;
            RefreshToggleCanExecute();
        }
    }

    private bool CanToggle => !_busy;

    private void RefreshToggleCanExecute()
    {
        StartTransferCommand.NotifyCanExecuteChanged();
        StopTransferCommand.NotifyCanExecuteChanged();
    }

    // ── 配置持久化 ──

    private void LoadConfig()
    {
        try
        {
            DeviceId = _discovery.LocalDeviceId;
            if (File.Exists(ConfigPath))
            {
                ModuleConfig? config = JsonSerializer.Deserialize<ModuleConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (config is not null)
                {
                    ReceiveDirectory = config.ReceiveDirectory ?? "";
                    RequireKnownPeer = config.RequireKnownPeer;
                    RequireReceiveConfirmation = config.RequireReceiveConfirmation;
                    ConflictPolicy = config.ConflictPolicy;
                    PauseTimeoutMinutes = config.PauseTimeoutMinutes > 0 ? config.PauseTimeoutMinutes : 30;
                    // 端口只在合法时采纳：配置文件被手改坏时退回默认值，而不是拿非法值去绑定
                    if (PortValidator.IsInRange(config.TcpPort))
                    {
                        TcpPortText = config.TcpPort.ToString();
                    }
                    if (PortValidator.IsInRange(config.DiscoveryPort))
                    {
                        DiscoveryPortText = config.DiscoveryPort.ToString();
                    }
                    KnownPeers.Clear();
                    foreach (KnownPeerEntry peer in config.KnownPeers)
                    {
                        KnownPeers.Add(new KnownPeerRowVm { Name = peer.Name, Ip = peer.Ip, Port = peer.Port });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log("[互传] ⚠️ 配置读取失败（使用默认值）：" + ex.Message);
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        if (TryPersistConfig(out string error))
        {
            _log("[互传] 配置已保存");
        }
        else
        {
            _log("[互传] ❌ 配置保存失败：" + error);
        }
    }

    /// <summary>
    /// 落盘配置（无日志，供端口/选项变更时的**静默自动保存**使用）。
    /// <para>
    /// 为什么要拆：端口是逐字符输入的，若每次变更都打一条「配置已保存」，日志会被刷成噪声；
    /// 但保存动作本身必须真的发生（否则"改完不生效"要等重启才发现）。
    /// </para>
    /// </summary>
    private bool TryPersistConfig(out string error)
    {
        try
        {
            var config = new ModuleConfig
            {
                ReceiveDirectory = ReceiveDirectory,
                RequireKnownPeer = RequireKnownPeer,
                RequireReceiveConfirmation = RequireReceiveConfirmation,
                ConflictPolicy = ConflictPolicy,
                PauseTimeoutMinutes = PauseTimeoutMinutes,
                TcpPort = TcpPort,
                DiscoveryPort = DiscoveryPort,
                KnownPeers = KnownPeers.Select(p => new KnownPeerEntry(p.Name, p.Ip, p.Port)).ToList(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOpts));
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private void AddKnownPeer()
    {
        // 带默认名并自动选中（审查 🔴-2 采纳）：避免完全空白行漏填漏存
        var row = new KnownPeerRowVm { Name = "新设备" };
        KnownPeers.Add(row);
        SelectedKnownPeer = row;
        _log("[互传] 已添加「新设备」并选中（填写 IP 后点保存）");
    }

    [RelayCommand]
    private void RemoveKnownPeer()
    {
        if (SelectedKnownPeer is not null)
        {
            KnownPeers.Remove(SelectedKnownPeer);
            RefreshDeviceKnownFlags(); // 移除后设备行应重新出现「加入已知」
            _ = TryPersistConfig(out _);
            _log($"[互传] 已移除已知设备：{SelectedKnownPeer.Name}");
        }
    }

    [RelayCommand]
    private void AddDiscoveredToKnown()
    {
        if (SelectedDevice is null)
        {
            _log("[互传] ⚠️ 请先选择要加入的设备");
            return;
        }

        string ip = SelectedDevice.Model.IPAddress.ToString();
        KnownPeerRowVm? existing = KnownPeers.FirstOrDefault(p => p.Ip == ip);
        if (existing is not null)
        {
            _log($"[互传] ⚠️ 该设备已在已知列表中（{existing.Name}）");
            return;
        }

        KnownPeers.Add(new KnownPeerRowVm
        {
            Name = SelectedDevice.Model.Name,
            Ip = ip,
            Port = SelectedDevice.Model.TransferPort,
        });
        RefreshDeviceKnownFlags();
        _log($"[互传] 已将 {SelectedDevice.Model.Name} 加入已知设备（记得点保存）");
    }

    // ── 设备发现 ──

    /// <summary>
    /// 一键把已发现设备加入「已知设备」（2026-09-13 批次 P3 ⑭）。
    /// <para>
    /// 与「添加」按钮的区别：那个要求用户手抄 IP，多网段 / 名字相近时很容易填错；
    /// 这里的 IP 与端口直接取自发现结果，不可能抄错。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void AddDeviceToKnown(DiscoveredDeviceRowVm? row)
    {
        if (row is null)
        {
            _log("[互传] ⚠️ 请先选择要加入的设备");
            return;
        }

        string ip = row.Model.IPAddress.ToString();
        int port = row.Model.TransferPort;
        if (KnownPeers.Any(p => p.Ip == ip && p.Port == port))
        {
            _log($"[互传] ⚠️ {row.Model.Name} 已在已知设备列表中");
            return;
        }

        KnownPeers.Add(new KnownPeerRowVm { Name = row.Model.Name, Ip = ip, Port = port });
        RefreshDeviceKnownFlags();
        _ = TryPersistConfig(out _); // 静默落盘：本操作意图明确，不该再要求用户点「保存」
        _log($"[互传] 已将 {row.Model.Name}（{ip}:{port}）加入已知设备");
    }

    /// <summary>重算每台已发现设备是否已在已知列表（设备列表刷新、已知列表增删后各调一次）。</summary>
    private void RefreshDeviceKnownFlags()
    {
        foreach (DiscoveredDeviceRowVm row in DiscoveredDevices)
        {
            string ip = row.Model.IPAddress.ToString();
            row.IsKnown = KnownPeers.Any(p => p.Ip == ip && p.Port == row.Model.TransferPort);
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChangeEventArgs e)
    {
        // 事件来自 UDP 回调线程 → 封送 UI 线程
        RunOnUi(() =>
        {
            DiscoveredDevices.Clear();
            foreach (DiscoveredDevice device in _discovery.Devices.OrderByDescending(d => d.IsOnline).ThenBy(d => d.Name))
            {
                DiscoveredDevices.Add(new DiscoveredDeviceRowVm(device));
            }

            RefreshDeviceKnownFlags();

            if (e.ChangeType == DeviceChangeType.Discovered)
            {
                _log($"[互传] 发现设备：{e.Device.Name}（{e.Device.IPAddress}）");
            }
        });
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (IsTransferRunning)
        {
            DiscoveredDevices.Clear();
            foreach (DiscoveredDevice device in _discovery.Devices.OrderByDescending(d => d.IsOnline).ThenBy(d => d.Name))
            {
                DiscoveredDevices.Add(new DiscoveredDeviceRowVm(device));
            }

            RefreshDeviceKnownFlags();

            // 如实给出"最近刷新时间"（本模块无"扫描中"状态，见 LastDeviceRefreshText 注释）
            LastDeviceRefreshText = "最近刷新 " + DateTime.Now.ToString(
                "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            _log($"[互传] 已刷新：{_discovery.Devices.Count} 台在线设备");
        }
        else
        {
            _log("[互传] ⚠️ 传输服务未启动——请先启动服务（同时开启设备发现）");
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    // ── 发送 ──

    /// <summary>View 调用：向指定设备发送所选文件（对话框多选 / 拖拽统一入口）。</summary>
    public async Task SendFilesToAsync(string ip, int port, IReadOnlyList<string> filePaths)
    {
        if (!IsTransferRunning)
        {
            _log("[互传] ⚠️ 传输服务未启动，请先启动服务");
            return;
        }

        foreach (string file in filePaths)
        {
            try
            {
                TransferTask task = await _transfer.SendFileAsync(file, ip, port).ConfigureAwait(true);
                _log($"[互传] 入队发送：{task.FileName} → {ip}:{port}");
            }
            catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
            {
                _log("[互传] ⚠ 操作已取消或超时。");
            }
            catch (Exception ex)
            {
                _log($"[互传] ❌ 发送失败（{Path.GetFileName(file)}）：" + ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task SendToSelectedDeviceAsync()
    {
        if (SelectedDevice is null)
        {
            _log("[互传] ⚠️ 请先选择目标设备");
            return;
        }

        IReadOnlyList<string>? files = PickFiles?.Invoke();
        if (files is not { Count: > 0 })
        {
            return;
        }

        await SendFilesToAsync(SelectedDevice.Model.IPAddress.ToString(), SelectedDevice.Model.TransferPort, files).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SendToKnownPeerAsync()
    {
        if (SelectedKnownPeer is null)
        {
            _log("[互传] ⚠️ 请先选择已知设备");
            return;
        }

        if (!System.Net.IPAddress.TryParse(SelectedKnownPeer.Ip, out _)
            || SelectedKnownPeer.Port is < 1 or > 65535)
        {
            _log("[互传] ⚠️ 已知设备的 IP/端口不合法");
            return;
        }

        IReadOnlyList<string>? files = PickFiles?.Invoke();
        if (files is not { Count: > 0 })
        {
            return;
        }

        await SendFilesToAsync(SelectedKnownPeer.Ip, SelectedKnownPeer.Port, files).ConfigureAwait(true);
    }

    /// <summary>把本机剪贴板内容读进输入框（读失败就地提示，不弹错误框）。</summary>
    [RelayCommand]
    private void PasteText()
    {
        string? text = null;
        try
        {
            text = ReadClipboard?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[互传] 读剪贴板失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _log("[互传] ⚠️ 剪贴板里没有文本内容（读失败或为空）");
            return;
        }

        TextToSend = text;
        _log($"[互传] 已从剪贴板读入 {TransferText.GetCharCount(text)} 字");
    }

    /// <summary>
    /// 发送一条文本（FT-3）。走与文件**同一套**目标解析与判据（<see cref="TransferText"/>）。
    /// <para>
    /// 🔴 成功后**不清空输入框**：一次发送失败（对端拒绝 / 剪贴板写失败）时用户还能直接重发，
    /// 清空等于让人把长文本再找回来一次。代价是可能重复点发，比重打一遍轻得多。
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendText))]
    private async Task SendText()
    {
        (string Ip, int Port)? target = ResolveTextTarget();
        if (target is null)
        {
            _log("[互传] ⚠️ 请先选择目标设备（局域网设备或已知设备）");
            return;
        }

        TextValidation validation = TransferText.Validate(TextToSend);
        if (!validation.IsValid)
        {
            _log($"[互传] ⚠️ {validation.ErrorText}");
            return;
        }

        try
        {
            TransferTask task = await _transfer
                .SendTextAsync(TextToSend, target.Value.Ip, target.Value.Port)
                .ConfigureAwait(true);
            _log($"[互传] 文本已入队发送（{validation.CharCount} 字 → {target.Value.Ip}:{target.Value.Port}，任务 {task.Id}）");
        }
        catch (Exception ex)
        {
            _log($"[互传] ⚠️ 文本发送失败：{ex.Message}");
            _logger.Warn($"[互传] 文本发送失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 文本发送目标：优先「局域网设备」选中项，否则「已知设备」选中项；都没有则 null。
    /// 与文件发送同一套取值来源，不做第二套选择逻辑。
    /// </summary>
    private (string Ip, int Port)? ResolveTextTarget()
    {
        if (SelectedDevice is not null)
        {
            return (SelectedDevice.Model.IPAddress.ToString(), SelectedDevice.Model.TransferPort);
        }

        if (SelectedKnownPeer is not null
            && System.Net.IPAddress.TryParse(SelectedKnownPeer.Ip, out _)
            && SelectedKnownPeer.Port is >= 1 and <= 65535)
        {
            return (SelectedKnownPeer.Ip, SelectedKnownPeer.Port);
        }

        return null;
    }

    // ── 任务 ──

    private void OnTaskUpdated(object? sender, TransferTask task)
    {
        RunOnUi(() =>
        {
            TransferTaskRowVm? row = ActiveTasks.FirstOrDefault(r => r.Model.Id == task.Id);
            if (row is null)
            {
                row = new TransferTaskRowVm(task);
                ActiveTasks.Insert(0, row);
            }
            else
            {
                row.Refresh();
            }

            if (row.IsActive)
            {
                EnsureTaskRefreshTimer();
            }
        });
    }

    private void OnTaskCompleted(object? sender, TransferTask task)
    {
        RunOnUi(() =>
        {
            TransferTaskRowVm? row = ActiveTasks.FirstOrDefault(r => r.Model.Id == task.Id);
            if (row is not null)
            {
                row.Refresh();
                ActiveTasks.Remove(row);
            }

            string direction = task.Direction == TransferDirection.Send ? "发送" : "接收";
            // 四态各自措辞：已跳过既不是"完成"（目标目录里没有新文件）也不是"失败"（过程没出错），
            // 混进任何一边都是状态欺骗
            _log(task.Status switch
            {
                TransferStatus.Completed => $"[互传] ✅ {direction}完成：{task.FileName}",
                TransferStatus.Skipped => $"[互传] ⏭ {direction}已跳过：{task.FileName}（同名文件已存在，未写入）",
                TransferStatus.Cancelled => $"[互传] 已取消：{task.FileName}"
                    + (string.IsNullOrEmpty(task.ReasonCode) ? string.Empty : $"（{TransferReasonCodes.Describe(task.ReasonCode)}）"),
                _ => $"[互传] ❌ {direction}失败：{task.FileName}"
                    + $"（{task.ErrorMessage}"
                    + (string.IsNullOrEmpty(task.ReasonCode) ? string.Empty : $" / {task.ReasonCode}") + "）",
            });

            _history.Append(new TransferHistoryEntry
            {
                TaskId = task.Id,
                FileName = task.FileName,
                FileSize = task.FileSize,
                Direction = task.Direction,
                PeerEndpoint = task.PeerEndpoint,
                Status = task.Status,
                StartedAt = task.StartedAt,
                FinishedAt = task.FinishedAt ?? DateTimeOffset.UtcNow,
                TransferredBytes = task.TransferredBytes,
                ErrorMessage = task.ErrorMessage,
                ReasonCode = task.ReasonCode,
            });
            ReloadHistory();
        });
    }

    private void EnsureTaskRefreshTimer()
    {
        if (_taskTimer is not null)
        {
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        timer.Tick += (_, _) =>
        {
            foreach (TransferTaskRowVm row in ActiveTasks)
            {
                row.Refresh();
            }

            if (ActiveTasks.All(r => !r.IsActive))
            {
                timer.Stop();
                _taskTimer = null;
            }
        };
        timer.Start();
        _taskTimer = timer;
    }

    private System.Windows.Threading.DispatcherTimer? _taskTimer;

    [RelayCommand]
    private async Task CancelTaskAsync(TransferTaskRowVm? row)
    {
        TransferTaskRowVm? target = row ?? SelectedTask;
        if (target is null)
        {
            _log("[互传] ⚠️ 请先选择要取消的任务"); // 🔴 不静默（审查 🟠-5 采纳）
            return;
        }

        // 审查 🔴 采纳（2026-09-09）：任务不存在/网络错误会抛——原实现无 catch，
        // 异常被 AsyncRelayCommand 吞掉，用户点了取消却看不到任何结果
        try
        {
            await _transfer.CancelAsync(target.Model.Id).ConfigureAwait(true);
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            _log("[互传] ⚠ 操作已取消或超时。");
        }
        catch (Exception ex)
        {
            _log($"[互传] ❌ 取消任务失败：{ex.Message}");
            _logger.Error($"取消传输任务失败（{target.Model.Id}）", ex);
        }
    }

    // ── 暂停 / 恢复（协议 §4.2，双向） ──

    /// <summary>
    /// 暂停或恢复任务（一个按钮位轮流承担：任务在跑就是"暂停"，已暂停就是"继续"）。
    /// <para>
    /// 受理结果**必须回话**：服务端对不可暂停的任务返回 false（状态不符/任务不存在），
    /// 用户点了没反应时至少日志里有依据，不静默。
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task TogglePauseAsync(TransferTaskRowVm? row)
    {
        TransferTaskRowVm? target = row ?? SelectedTask;
        if (target is null)
        {
            _log("[互传] ⚠️ 请先选择要暂停/恢复的任务");
            return;
        }

        bool resume = target.CanResume;
        try
        {
            bool handled = resume
                ? await _transfer.ResumeTaskAsync(target.Model.Id).ConfigureAwait(true)
                : await _transfer.PauseTaskAsync(target.Model.Id).ConfigureAwait(true);
            if (!handled)
            {
                _log($"[互传] ⚠️ {(resume ? "恢复" : "暂停")}未被受理：任务当前状态为 {target.StatusText}");
            }
            else
            {
                _log($"[互传] {(resume ? "已恢复" : "已暂停")}：{target.FileName}");
                target.Refresh();
            }
        }
        catch (OperationCanceledException) // v5 B1：取消/超时不伪装为业务失败
        {
            _log("[互传] ⚠ 操作已取消或超时。");
        }
        catch (Exception ex)
        {
            _log($"[互传] ❌ {(resume ? "恢复" : "暂停")}任务失败：{ex.Message}");
            _logger.Error($"{(resume ? "恢复" : "暂停")}传输任务失败（{target.Model.Id}）", ex);
        }
    }

    // ── 接收确认门 ──

    private void OnTransferRequested(object? sender, TransferRequestEventArgs e)
    {
        // 事件来自 WatsonTcp 回调线程：确认弹窗必须在 UI 线程
        RunOnUi(() =>
        {
            TransferDecision decision = e.Kind == TransferKind.Text
                ? ConfirmIncomingText(e)
                : ConfirmIncomingFile(e);

            // 审查 🟠-2 采纳（2026-09-09）：fire-and-forget 的响应失败原本完全无痕——
            // 断网时用户点了「接受」但对面毫无反应，排查无从下手；补日志落地
            _ = _transfer.RespondTransferAsync(e.TaskId, decision)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        string reason = t.Exception?.InnerException?.Message ?? "未知原因";
                        _log($"[互传] ⚠️ 发送接收响应失败：{reason}");
                        _logger.Warn($"[互传] 接收响应发送失败（task={e.TaskId}）：{reason}");
                    }
                }, TaskScheduler.Default);
        });
    }

    /// <summary>
    /// 文件通道：首选富信息对话框（能看到存到哪、盘剩多少、同名会怎样）；
    /// View 未注入时退回简单确认——保证"没有对话框也不能默默拒绝"。
    /// </summary>
    private TransferDecision ConfirmIncomingFile(TransferRequestEventArgs e)
    {
        if (ConfirmTransferRequest is not null)
        {
            return ConfirmTransferRequest(e);
        }

        bool accept = ConfirmRequest?.Invoke(
            "接收文件请求",
            $"{e.PeerEndpoint} 想向你发送文件：\n\n「{e.FileName}」（{e.FileSize:N0} 字节）\n\n接受吗？"
            + "\n\n（不做任何响应则自动超时拒绝）") == true;
        return accept ? TransferDecision.AcceptWith(TransferConflictPolicy.Rename) : TransferDecision.Reject;
    }

    /// <summary>
    /// 文本通道：先问用户 → 再**写剪贴板** → 据实回执。
    /// <para>
    /// 🔴 写剪贴板成功与否直接决定回执（见 <see cref="ReceiveTextDecision"/>）：
    /// 「接收文本」就是「写剪贴板」，写不进去必须回 <c>CLIPBOARD_WRITE_FAILED</c>，
    /// 绝不能让对端收到"已送达"而用户剪贴板里什么都没有。
    /// </para>
    /// </summary>
    private TransferDecision ConfirmIncomingText(TransferRequestEventArgs e)
    {
        bool accepted;
        if (ConfirmTransferRequest is not null)
        {
            // 富对话框已按 Kind 切成文本形态（全文 + 字数），这里只取"用户是否同意"
            accepted = ConfirmTransferRequest(e).Accept;
        }
        else
        {
            accepted = ConfirmRequest?.Invoke(
                "收到一条文本",
                $"{e.PeerEndpoint} 想向你发送一条文本（{e.TextLength} 字）：\n\n"
                + $"{TransferText.Preview(e.Text)}\n\n"
                + "接收后会写入你的剪贴板（会覆盖当前内容）。接受吗？\n\n（不做任何响应则自动超时拒绝）") == true;
        }

        if (!accepted)
        {
            return ReceiveTextDecision.Resolve(accepted: false, clipboardWritten: false);
        }

        bool wrote = false;
        if (WriteClipboard is null || e.Text is null)
        {
            _logger.Warn($"[互传] 文本无法交付（未注入剪贴板写入能力或内容为空，task={e.TaskId}）——按写入失败处理。");
        }
        else
        {
            try
            {
                wrote = WriteClipboard(e.Text);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[互传] 写剪贴板抛异常（task={e.TaskId}）：{ex.Message}");
            }
        }

        if (!wrote)
        {
            _log("[互传] ⚠️ 剪贴板写入失败，本次文本未接收（已如实告知对方原因）。");
        }

        return ReceiveTextDecision.Resolve(accepted: true, clipboardWritten: wrote);
    }

    // ── 历史 ──

    /// <summary>历史列表的筛选视图（P3 ⑮）：筛选只影响显示，不动存储。</summary>
    public System.ComponentModel.ICollectionView HistoryView { get; }

    /// <summary>方向筛选项（首项 = 不限）。</summary>
    public IReadOnlyList<string> HistoryDirectionOptions { get; } = ["全部方向", "仅发送", "仅接收"];

    /// <summary>状态筛选项（首项 = 不限；与任务状态一一对应）。</summary>
    public IReadOnlyList<string> HistoryStatusOptions { get; } = ["全部状态", "完成", "失败", "已跳过", "已取消"];

    [ObservableProperty]
    private string _historyDirectionFilter = "全部方向";

    [ObservableProperty]
    private string _historyStatusFilter = "全部状态";

    [ObservableProperty]
    private string _historySearchText = "";

    partial void OnHistoryDirectionFilterChanged(string value) => HistoryView.Refresh();

    partial void OnHistoryStatusFilterChanged(string value) => HistoryView.Refresh();

    partial void OnHistorySearchTextChanged(string value) => HistoryView.Refresh();

    private bool FilterHistory(object item)
    {
        if (item is not TransferHistoryEntry entry)
        {
            return false;
        }

        if (HistoryDirectionFilter == "仅发送" && entry.Direction != TransferDirection.Send)
        {
            return false;
        }
        if (HistoryDirectionFilter == "仅接收" && entry.Direction != TransferDirection.Receive)
        {
            return false;
        }
        if (HistoryStatusFilter != "全部状态" && entry.StatusText != HistoryStatusFilter)
        {
            return false;
        }

        string keyword = HistorySearchText?.Trim() ?? string.Empty;
        if (keyword.Length > 0
            && entry.FileName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
            && entry.PeerEndpoint.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 导出**当前筛选结果**为 CSV（P3 ⑮）。
    /// <para>
    /// 导出的是"屏幕上的那批"而不是全部：用户先筛出失败的几笔再导出，是最常见的用法；
    /// 若导出全部，他会得到一份与眼前不一致的文件——那比没有导出更糟。
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ExportHistoryCsv()
    {
        var rows = HistoryView.Cast<TransferHistoryEntry>().ToList();
        if (rows.Count == 0)
        {
            _log("[互传] ⚠️ 当前筛选结果为空，没有可导出的记录");
            return;
        }

        if (PickExportPath is null)
        {
            _log("[互传] ⚠️ 导出不可用（未接入保存对话框）");
            return;
        }

        string? path = PickExportPath($"{TransferHistoryCsv.FileNamePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path))
        {
            return; // 用户取消，不是错误
        }

        try
        {
            // 🔴 带 BOM 的 UTF-8：不加的话 Excel 打开中文列名与文件名会变乱码
            // （用 AtomicFile.WriteAllBytes 满足本仓"新增文件写入必须走 AtomicFile"的守卫）
            AtomicFile.WriteAllBytes(path, new System.Text.UTF8Encoding(true).GetBytes(TransferHistoryCsv.Build(rows)));
            _log($"[互传] ✅ 已导出 {rows.Count} 条历史：{path}");
        }
        catch (Exception ex)
        {
            _log($"[互传] ❌ 导出失败：{ex.Message}");
            _logger.Error("导出传输历史失败", ex);
        }
    }

    /// <summary>导出保存对话框回调（View 注入；参数为建议文件名，返回 null = 用户取消）。</summary>
    public Func<string, string?>? PickExportPath { get; set; }

    private void ReloadHistory()
    {
        HistoryEntries.Clear();
        foreach (TransferHistoryEntry entry in _history.Load())
        {
            HistoryEntries.Add(entry);
        }

        HistoryView.Refresh(); // 重新加载后按当前筛选条件重算
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (ConfirmRequest?.Invoke("清空传输历史", $"确定清空全部 {HistoryEntries.Count} 条历史记录吗？此操作不可撤销。") != true)
        {
            return;
        }

        _history.Clear();
        ReloadHistory();
        _log("[互传] 传输历史已清空");
    }

    /// <summary>模块配置（JSON 持久化）。</summary>
    private sealed class ModuleConfig
    {
        public string? ReceiveDirectory { get; set; }

        public bool RequireKnownPeer { get; set; } = true;

        public bool RequireReceiveConfirmation { get; set; } = true;

        /// <summary>同名冲突策略（2026-09-13 批次 P1；缺省时按 Rename）。</summary>
        public TransferConflictPolicy ConflictPolicy { get; set; } = TransferConflictPolicy.Rename;

        /// <summary>暂停超时（分钟；0 或负 = 不限）。</summary>
        public int PauseTimeoutMinutes { get; set; } = 30;

        /// <summary>电脑通道 TCP 端口（2026-09-13 批次 P3 ⑱）。</summary>
        public int TcpPort { get; set; } = 18889;

        /// <summary>设备发现 UDP 端口。</summary>
        public int DiscoveryPort { get; set; } = 18888;

        public List<KnownPeerEntry> KnownPeers { get; set; } = new();
    }

    private sealed record KnownPeerEntry(string Name, string Ip, int Port);

    /// <summary>同名冲突策略的下拉项（值 + 中文标签）。</summary>
    public sealed record ConflictPolicyOption(TransferConflictPolicy Value, string Text);
}
