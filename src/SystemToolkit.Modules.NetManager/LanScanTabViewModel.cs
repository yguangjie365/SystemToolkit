using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Network.LanScan;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 「局域网扫描」Tab（NET-6）：主动扫段 + 基线 diff + IP 冲突监控。布局契约见已批准示意图
/// <c>Docs/70-原型与提示词/局域网扫描-NET6示意图.html</c>（①主界面 ②空态 ③失败态）。
/// <para>
/// 全程免提权（调研定稿）；自动监控默认关、5 分钟一轮；扫描可取消（取消轮不提交基线）。
/// 🔴 本文件禁止 OnXxxChanged partial 钩子（_wpftmp 通道 CS0759 教训）——联动一律在赋值点手工接线。
/// </para>
/// </summary>
public partial class LanScanTabViewModel : ObservableObject
{
    /// <summary>自动监控周期（示意图标注 3️⃣：默认关，开启后每 5 分钟）。</summary>
    public static readonly TimeSpan MonitorInterval = TimeSpan.FromMinutes(5);

    private readonly INetworkInfoService _info;
    private readonly LanScanService _scan;
    private readonly Action<string> _log;
    private readonly ILanScanAlertStore? _alertStore;
    private readonly LanScanAlertNotifier? _notifier;
    private CancellationTokenSource? _monitorCts;
    private LanScanResult? _lastResult;
    private string _filter = string.Empty;

    /// <summary>载入告警配置期间抑制落盘——否则"读出来的值"会被 setter 立刻写回去。</summary>
    private bool _suppressAlertPersist;

    /// <summary>
    /// 后两个参数**可选**：既有调用方（含 <c>ViewLoadSmokeGuardTests</c>）不传也能编译，
    /// 此时告警区退化为"未接入"（全关、不落盘、不外呼）；真实宿主经 DI 喂实现。
    /// </summary>
    public LanScanTabViewModel(
        INetworkInfoService info,
        LanScanService scan,
        Action<string> log,
        ILanScanAlertStore? alertStore = null,
        LanScanAlertNotifier? notifier = null)
    {
        _info = info;
        _scan = scan;
        _log = log;
        _alertStore = alertStore;
        _notifier = notifier;
        LoadAlertConfig();
    }

    /// <summary>行定位请求（View 订阅：滚动设备表到冲突 IP）。</summary>
    public event Action<string>? LocateRequested;

    // ══════════════ 适配器选择 ══════════════

    public ObservableCollection<AdapterRowVm> Adapters { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private AdapterRowVm? _selectedAdapter;

    private bool CanScan => !IsBusy && SelectedAdapter is not null;

    // ══════════════ 扫描状态 ══════════════

    [ObservableProperty]
    // 🟠 G-🟠-1 v11~v14 后续批次：原先无通知 ⇒ IsBusy 置位时按钮保持可点，
    // 而命令体又没有 if (IsBusy) 守卫（RelayCommand.Execute 不查 CanExecute）
    // ⇒ 双击必然并发执行。命令在结束时的手工通知保留（双通知无害），此处补齐入口侧。
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isBusy;

    /// <summary>扫描进行中（进度条与「取消」按钮可见性；区别于监控静默轮）。</summary>
    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _lastScanText = "尚未扫描";

    [ObservableProperty]
    private bool _hasLoaded;

    /// <summary>已有一轮完整结果（空态 = !HasData；失败态 = HasLoaded 且无可用适配器）。</summary>
    public bool HasData => _lastResult is not null;

    public bool NoUsableAdapter => HasLoaded && Adapters.Count == 0;

    // 三态可见性（System.Windows.Visibility 直出——本视图域先例：FileTransfer VM 用 Clipboard）
    public Visibility TableVis => HasData ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVis => HasData || NoUsableAdapter ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FailedVis => NoUsableAdapter && !HasData ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseStateVisibility()
    {
        OnPropertyChanged(nameof(TableVis));
        OnPropertyChanged(nameof(EmptyVis));
        OnPropertyChanged(nameof(FailedVis));
    }

    // ══════════════ 冲突横幅（示意图 ① banner） ══════════════

    [ObservableProperty]
    private bool _bannerVisible;

    [ObservableProperty]
    private string _bannerText = string.Empty;

    [ObservableProperty]
    private string _bannerDetail = string.Empty;

    // ══════════════ 统计芯片 ══════════════

    [ObservableProperty]
    private int _deviceCount;

    [ObservableProperty]
    private int _onlineCount;

    [ObservableProperty]
    private int _conflictCount;

    [ObservableProperty]
    private int _changedCount;

    [ObservableProperty]
    private int _newCount;

    [ObservableProperty]
    private int _offlineCount;

    // ══════════════ 集合与筛选 ══════════════

    public ObservableCollection<LanDeviceRow> Rows { get; } = new();

    public ObservableCollection<LanEventRow> Events { get; } = new();

    /// <summary>搜索框（IP/MAC/主机名/厂商 包含匹配）。赋值点手工重建行——禁 partial 钩子。</summary>
    public string FilterText
    {
        get => _filter;
        set
        {
            if (_filter == value)
            {
                return;
            }

            _filter = value ?? string.Empty;
            RebuildRows();
            OnPropertyChanged();
        }
    }

    // ══════════════ 自动监控 ══════════════

    [ObservableProperty]
    private bool _isMonitorOn;

    /// <summary>🟠 V16-1：用户意图位 —— 打开监控置 true、关闭置 false。
    /// 「页面卸载取消」（<see cref="CancelMonitor"/>）**不改**它（反模式 ㊷ 双重翻转）。</summary>
    private bool _monitorUserWantsOn;

    /// <summary>精确 OS 识别开关（NET-6 增强）：开启后每轮扫描对 Windows 候选发起一次批量
    /// 远程 WMI（经 ElevatedHelper，一轮一次 UAC）；关闭/拒绝时保留 TTL 推断值。</summary>
    [ObservableProperty]
    private bool _isPreciseOs;

    // ══════════════ 告警推送配置（B3-③） ══════════════
    // 🔴 本文件禁止 OnXxxChanged partial 钩子（_wpftmp 通道 CS0759 教训）→ 手工属性 + 赋值点接线。
    // 无「保存」按钮：改动即落盘（URL 走 LostFocus 触发，不会逐字符写盘）。

    private bool _isAlertEnabled;
    private string _alertWebhookUrl = string.Empty;
    private bool _alertOnConflict = true;
    private bool _alertOnBindingChanged;
    private bool _alertOnNewDevice;
    private string _alertStatusText = "尚未推送";

    /// <summary>推送总开关（默认关——外呼能力必须由用户显式开启）。</summary>
    public bool IsAlertEnabled
    {
        get => _isAlertEnabled;
        set
        {
            if (SetProperty(ref _isAlertEnabled, value))
            {
                PersistAlertConfig();
            }
        }
    }

    /// <summary>机器人 Webhook 地址（钉钉 / 企业微信），落盘时经 DPAPI 加密。</summary>
    public string AlertWebhookUrl
    {
        get => _alertWebhookUrl;
        set
        {
            if (SetProperty(ref _alertWebhookUrl, value ?? string.Empty))
            {
                PersistAlertConfig();
            }
        }
    }

    /// <summary>IP 冲突是否推送（默认勾选——这是立项场景本身）。</summary>
    public bool AlertOnConflict
    {
        get => _alertOnConflict;
        set
        {
            if (SetProperty(ref _alertOnConflict, value))
            {
                PersistAlertConfig();
            }
        }
    }

    /// <summary>IP↔MAC 绑定变更是否推送（默认不勾，防"一开就刷屏"）。</summary>
    public bool AlertOnBindingChanged
    {
        get => _alertOnBindingChanged;
        set
        {
            if (SetProperty(ref _alertOnBindingChanged, value))
            {
                PersistAlertConfig();
            }
        }
    }

    /// <summary>新设备发现是否推送（默认不勾，同上）。</summary>
    public bool AlertOnNewDevice
    {
        get => _alertOnNewDevice;
        set
        {
            if (SetProperty(ref _alertOnNewDevice, value))
            {
                PersistAlertConfig();
            }
        }
    }

    /// <summary>最近一次推送结果（含"未外呼"的原因）。</summary>
    public string AlertStatusText
    {
        get => _alertStatusText;
        private set => SetProperty(ref _alertStatusText, value);
    }

    /// <summary>
    /// 启用了、填了 URL、但格式不合法时就地提示（空串 = 不提示）。
    /// 配错**不会**外呼，但必须让用户看见——静默吞掉非法输入是本仓的既有痛点。
    /// </summary>
    public string AlertUrlError =>
        _isAlertEnabled && !string.IsNullOrWhiteSpace(_alertWebhookUrl) && !BuildAlertConfig().HasValidUrl
            ? "URL 需为 http:// 或 https:// 开头的完整地址"
            : string.Empty;

    /// <summary>
    /// 错误行的可见性。直出 <see cref="Visibility"/> 而非引转换器——本视图域既有先例
    /// （<c>TableVis</c>/<c>EmptyVis</c>/<c>FailedVis</c> 同款）。
    /// </summary>
    public Visibility AlertUrlErrorVis =>
        AlertUrlError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private LanScanAlertConfig BuildAlertConfig() => new(
        _isAlertEnabled,
        string.IsNullOrWhiteSpace(_alertWebhookUrl) ? null : _alertWebhookUrl.Trim(),
        _alertOnConflict,
        _alertOnBindingChanged,
        _alertOnNewDevice);

    /// <summary>载入已存配置（缺省/损坏 → 全关）。</summary>
    private void LoadAlertConfig()
    {
        LanScanAlertConfig config = _alertStore?.Load() ?? LanScanAlertConfig.Default;
        _suppressAlertPersist = true;
        try
        {
            _isAlertEnabled = config.Enabled;
            _alertWebhookUrl = config.WebhookUrl ?? string.Empty;
            _alertOnConflict = config.OnConflict;
            _alertOnBindingChanged = config.OnBindingChanged;
            _alertOnNewDevice = config.OnNewDevice;
        }
        finally
        {
            _suppressAlertPersist = false;
        }

        OnPropertyChanged(nameof(IsAlertEnabled));
        OnPropertyChanged(nameof(AlertWebhookUrl));
        OnPropertyChanged(nameof(AlertOnConflict));
        OnPropertyChanged(nameof(AlertOnBindingChanged));
        OnPropertyChanged(nameof(AlertOnNewDevice));
        OnPropertyChanged(nameof(AlertUrlError));
        OnPropertyChanged(nameof(AlertUrlErrorVis));
    }

    private void PersistAlertConfig()
    {
        OnPropertyChanged(nameof(AlertUrlError));
        OnPropertyChanged(nameof(AlertUrlErrorVis));
        if (_suppressAlertPersist)
        {
            return;
        }

        _alertStore?.Save(BuildAlertConfig());
    }

    /// <summary>
    /// 手动发送测试：**忽略三个事件类型勾选**（测试要验的是 URL 与网络通不通），
    /// 但仍受总开关与 URL 闸门约束——否则这个按钮就成了一条绕过闸门的外呼路径。
    /// </summary>
    [RelayCommand]
    private async Task SendTestAlertAsync()
    {
        try
        {
            if (_notifier is null)
            {
                // 🟡 V14-N10：本分支原先只写共享日志面板，**不**改状态行——而状态行显示的是
                // "最近一次推送结果"，用户点「发送测试」后看到的是**上一次**的结果（甚至初始空串），
                // 与刚刚发生的事不符。成功/异常两个分支都写状态行，这里补齐同口径。
                AlertStatusText = "告警外呼未接入";
                _log("[局域网] ⚠ 告警外呼未接入（无可用外呼组件）");
                return;
            }

            LanScanAlertConfig testConfig = BuildAlertConfig() with
            {
                OnConflict = true,
                OnBindingChanged = true,
                OnNewDevice = true,
            };
            var probe = new List<LanEvent>
            {
                new(LanEventType.Conflict, "192.168.1.7", "3C:00:00:00:00:1F", "9A:00:00:00:00:04",
                    "测试消息（由「发送测试」触发，忽略事件类型勾选）", DateTimeOffset.Now),
            };

            LanAlertSendResult result = await _notifier.SendAsync(testConfig, probe).ConfigureAwait(true);
            AlertStatusText = $"最近一次：{result.Message} · {DateTime.Now:HH:mm:ss}";
            _log($"[局域网] 告警测试 → {result.Message}");
        }
        catch (Exception ex)
        {
            AlertStatusText = $"测试异常：{ex.Message}";
            _log("[局域网] ❌ 告警测试异常：" + ex.Message);
        }
    }

    /// <summary>导出保存对话框回调（View 注入；参数为建议文件名，返回 null = 用户取消）。</summary>
    public Func<string, string?>? PickExportPath { get; set; }

    /// <summary>
    /// 导出**全部已持久化事件**（基线环 ≤ <see cref="LanBaselineStore.MaxEvents"/> 条），
    /// 而不是界面显示的 50 条——取证/上报要的是完整留存（按钮 ToolTip 已写明范围）。
    /// </summary>
    [RelayCommand]
    private void ExportEventsCsv()
    {
        try
        {
            IReadOnlyList<LanEvent> events = _scan.PeekBaseline()?.Events
                ?? _lastResult?.Events
                ?? (IReadOnlyList<LanEvent>)Array.Empty<LanEvent>();
            if (events.Count == 0)
            {
                _log("[局域网] ⚠ 暂无事件可导出（先扫一轮）");
                return;
            }

            if (PickExportPath is null)
            {
                _log("[局域网] ⚠ 导出不可用（未接入保存对话框）");
                return;
            }

            string? path = PickExportPath($"{LanEventCsv.FileNamePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            if (string.IsNullOrEmpty(path))
            {
                return; // 用户取消，不是错误
            }

            // 🔴 带 BOM 的 UTF-8：不加的话 Excel 打开中文列名与详情会变乱码；
            // 走 AtomicFile 满足"新增文件写入必须走 AtomicFile"的守卫。
            AtomicFile.WriteAllBytes(
                path,
                new UTF8Encoding(true).GetBytes(LanEventCsv.Build(events, BuildDeviceIndex())));
            _log($"[局域网] ✅ 已导出 {events.Count} 条事件：{path}");
        }
        catch (Exception ex)
        {
            _log($"[局域网] ❌ 导出失败：{ex.Message}");
        }
    }

    /// <summary>事件 → 设备索引（把厂商/主机名补进 CSV）。没有本轮结果就返回 null，不臆造字段。</summary>
    private IReadOnlyDictionary<string, LanDevice>? BuildDeviceIndex()
    {
        if (_lastResult is null || _lastResult.Devices.Count == 0)
        {
            return null;
        }

        var index = new Dictionary<string, LanDevice>(StringComparer.Ordinal);
        foreach (LanDevice device in _lastResult.Devices)
        {
            index[device.Ip] = device;
        }

        return index;
    }

    /// <summary>
    /// 按配置推送本轮事件。fire-and-forget：外呼**绝不阻塞**扫描主流程；
    /// 未启用/未配 URL 时连调用都不发起（零外呼路径的第一道闸）。
    /// </summary>
    private async Task PushAlertAsync(LanScanResult result)
    {
        try
        {
            if (_notifier is null)
            {
                return;
            }

            LanScanAlertConfig config = BuildAlertConfig();
            if (!config.CanSend)
            {
                return;
            }

            LanAlertSendResult outcome = await _notifier.SendAsync(config, result.Events).ConfigureAwait(true);
            if (outcome.Attempted)
            {
                AlertStatusText = $"最近一次：{outcome.Message} · {DateTime.Now:HH:mm:ss}";
                _log($"[局域网] 告警推送 → {outcome.Message}");
            }
        }
        catch (Exception ex)
        {
            // fire-and-forget 的异常无人 await：必须在此吞掉并留痕，否则会变成未观察异常
            _log("[局域网] ⚠ 告警推送异常（不影响扫描）：" + ex.Message);
        }
    }

    // ══════════════ 命令 ══════════════

    /// <summary>页面首载：拉适配器（复用设置页同源数据），默认选中第一块可用 IPv4 物理网卡。</summary>
    public async Task LoadAsync()
    {
        try
        {
            IReadOnlyList<NetAdapterInfo> all = await _info.GetAdaptersAsync().ConfigureAwait(true);
            Adapters.Clear();
            foreach (NetAdapterInfo adapter in all.Where(HasScannableIpv4))
            {
                Adapters.Add(new AdapterRowVm(adapter));
            }

            SelectedAdapter ??= Adapters.FirstOrDefault();
            HasLoaded = true;
            OnPropertyChanged(nameof(NoUsableAdapter));
            RaiseStateVisibility();
            ScanCommand.NotifyCanExecuteChanged();
            _log(Adapters.Count == 0
                ? "[局域网] ⚠ 未检测到可用 IPv4 适配器（虚拟网卡与 APIPA 已排除）"
                : $"[局域网] 可扫描适配器 {Adapters.Count} 块（免提权：SendARP + 邻居表）");
            if (_lastResult is null)
            {
                LoadExistingBaseline();
            }
        }
        catch (Exception ex)
        {
            HasLoaded = true;
            _log("[局域网] ❌ 适配器读取异常：" + ex.Message);
        }
    }

    /// <summary>从未扫描过时补齐既有基线的**事件流**，避免首屏全空丢历史。
    /// <para>
    /// ⚠️ 本方法**只填 <c>Events</c>，不填 <c>Rows</c>** —— 设备表格仍走空态，要等一次真实扫描
    /// （<c>TableVis</c> / <c>EmptyVis</c> 的判据是 <c>HasData = _lastResult is not null</c>）。
    /// 🟡 G-🟠-2 v11~v14 后续批次订正：原注释称"展示既有基线设备（灰/离线形态）"、
    /// 原日志称"已加载历史基线 N 台"，都与实现不符（既没建 <c>Rows</c> 也没置 <c>_lastResult</c>）
    /// ⇒ 用户会遇到"日志说加载了 15 台、界面却提示还没扫描"这对矛盾。
    /// 若要真正兑现"灰/离线形态"，需同时改 <c>HasData</c> 判据并构造 Offline 行（留待后续评估）。
    /// </para>
    /// </summary>
    private void LoadExistingBaseline()
    {
        LanBaseline? baseline = _scan.PeekBaseline();
        if (baseline is null || baseline.Entries.Count == 0)
        {
            return;
        }

        _log($"[局域网] 已载入历史基线的事件记录 {baseline.Events.Count} 条（来自上次运行）；"
            + $"设备列表（{baseline.Entries.Count} 台）需点「重新扫描」后展示");
        Events.Clear();
        foreach (LanEvent evt in baseline.Events.Take(50))
        {
            Events.Add(new LanEventRow(evt));
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        // 🟠 G-🟠-1：命令体兜底守卫——RelayCommand.Execute **不检查 CanExecute**，
        // CanExecute 的拦截完全依赖 Button.IsEnabled（只在通知触发时重询）。双击是极常见操作，
        // 一旦通知失效即并发进方法体：后到者覆盖 _runCts、两轮结果互相覆盖、取消失效。
        if (IsBusy)
        {
            return;
        }

        LanSubnetPlan? plan = BuildPlan(out string planError);
        if (plan is null)
        {
            _log("[局域网] ❌ " + planError);
            return;
        }

        IsBusy = true;
        IsScanning = true;
        ProgressValue = 0;
        ProgressText = $"0/{plan.Hosts.Count}";
        CancellationTokenSource runCts = new();
        _runCts = runCts;
        IProgress<LanScanProgress> progress = new Progress<LanScanProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
            ProgressText = $"{p.Phase} {p.Done}/{p.Total}";
        });
        try
        {
            DateTimeOffset startedAt = DateTimeOffset.Now;
            LanScanResult result = await _scan.ScanAsync(plan, progress, runCts.Token, preciseOs: IsPreciseOs)
                .ConfigureAwait(true);
            if (result.WasCancelled)
            {
                LastScanText = "本轮已取消（基线未变更）";
                _log("[局域网] 扫描取消：本轮不提交，基线未更新");
                return;
            }

            _lastResult = result;
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(NoUsableAdapter));
            RaiseStateVisibility();
            ApplyResult(result, (int)(DateTimeOffset.Now - startedAt).TotalSeconds);
        }
        catch (Exception ex)
        {
            // 审查 🟠-2 同款：AsyncRelayCommand 会吞异常，必须用户可见
            _log("[局域网] ❌ 扫描异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
            // 🟠 V14-N4：置 null 前先 Cancel + Dispose——Cancel 只是置位，注册句柄（Task.Delay /
            // 传输层内部 linked source）要 Dispose 才释放；只置 null 会让 CTS 及其注册留在
            // 终结器队列里，反复扫描逐轮累积（同款纪律见 DriverManagerViewModel 的 V12-D3）。
            // 🟡 G-🟡-3 v11~v14 后续批次：加身份判定（V12-D3 同款）——并发场景下后到者
            // 已把 _runCts 换成自己的实例，此时直接置 null 会清掉**别人**的引用，
            // 令其后续取消失效（G-🟠-1g 的内部守卫已挡住并发，此处是纵深防御）。
            if (ReferenceEquals(_runCts, runCts))
            {
                try
                {
                    runCts.Cancel();
                }
                finally
                {
                    runCts.Dispose();
                    _runCts = null;
                }
            }

            ScanCommand.NotifyCanExecuteChanged();
        }
    }

    private CancellationTokenSource? _runCts;

    [RelayCommand]
    private void CancelScan()
    {
        _runCts?.Cancel();
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    /// <summary>自动监控开关：ToggleButton 翻转后进入——开=起循环，关=取消并复位。</summary>
    [RelayCommand]
    private void ToggleMonitor()
    {
        if (IsMonitorOn)
        {
            _monitorUserWantsOn = true; // 🟠 V16-1：用户意图（卸载取消不改它）
            _monitorCts = new CancellationTokenSource();
            _log($"[局域网] 自动监控已开启（每 {MonitorInterval.TotalMinutes:0} 分钟一轮，可取消）");
            _ = MonitorLoopAsync(_monitorCts.Token);
        }
        else
        {
            // 🟠 V14-N4：同上——停监控时必须 Cancel **并** Dispose（先 Cancel 让循环尽快退出，
            // Dispose 释放注册句柄），否则每次「开→关」都留下一个未释放的 CTS。
            if (_monitorCts is { } cts)
            {
                try
                {
                    cts.Cancel();
                }
                finally
                {
                    cts.Dispose();
                    _monitorCts = null;
                }
            }

            _monitorUserWantsOn = false; // 🟠 V16-1：用户主动关闭 ⇒ 意图随之作废
            _log("[局域网] 自动监控已关闭");
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(MonitorInterval, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (IsBusy)
                {
                    continue; // 手动扫描在途：让路
                }

                LanSubnetPlan? plan = BuildPlan(out _);
                if (plan is null)
                {
                    break;
                }

                IsBusy = true;
                try
                {
                    LanScanResult result = await _scan.ScanAsync(plan, null, ct, preciseOs: IsPreciseOs)
                        .ConfigureAwait(true);
                    if (!result.WasCancelled)
                    {
                        ApplyResult(result, silent: true);
                        if (result.ConflictedIps.Count > 0)
                        {
                            _log($"[局域网] ⚠ 监控检出 IP 冲突：{string.Join("、", result.ConflictedIps)}");
                        }
                    }
                }
                finally
                {
                    IsBusy = false;
                    ScanCommand.NotifyCanExecuteChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停表
        }
        catch (Exception ex)
        {
            _log("[局域网] ❌ 监控循环异常终止：" + ex.Message);
            IsMonitorOn = false;
        }
    }

    /// <summary>页面卸载/切走时停监控（Diagnostics.CancelPing 同款纪律，审查 O7）。
    /// <para>
    /// 🟠 V16-1（2026-09-15）：批② 把 View 改 Transient 后**主题切换也会重建视图并触发 Unloaded**
    /// ⇒ 本方法被复用为两种语义，后者会把用户开的自动监控永久停掉。
    /// 处置**不是删本方法**（O7 防泄漏是正确纪律），而是**不动用户意图位**，
    /// 由 <see cref="ResumeMonitorIfIntended"/> 按意图恢复。
    /// </para></summary>
    public void CancelMonitor()
    {
        if (!IsMonitorOn)
        {
            return;
        }

        IsMonitorOn = false;
        // 🟠 V14-N4：切走页面即停监控——同样 Cancel + Dispose（页面上再不回来，注册句柄
        // 若只置 null 就再没人释放它）。
        if (_monitorCts is { } cts)
        {
            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
                _monitorCts = null;
            }
        }
    }

    /// <summary>🟠 V16-1：视图重新加载（含主题切换重建）时按**用户意图**恢复自动监控。
    /// 判据两条：用户想让监控开（<c>_monitorUserWantsOn</c>）**且**当前没在开（<c>!IsMonitorOn</c>）。</summary>
    public void ResumeMonitorIfIntended()
    {
        if (!_monitorUserWantsOn || IsMonitorOn)
        {
            return;
        }

        IsMonitorOn = true;                 // 先复位开关：ToggleButton 双向绑定随之回到「开」
        ToggleMonitorCommand.Execute(null); // 走既有命令，不复制启动逻辑
    }

    [RelayCommand]
    private void CopyRow(LanDeviceRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText($"{row.Ip}\t{row.Mac}");
            _log($"[局域网] 已复制：{row.Ip} {row.Mac}");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _log("[局域网] ⚠ 剪贴板占用中，复制失败");
        }
    }

    /// <summary>行内 Ping：真实 ICMP 判定直接落本页操作日志（用户 09-12 反馈：不再跳诊断 Tab）。</summary>
    [RelayCommand]
    private async Task PingRowAsync(LanDeviceRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            string verdict = await _scan.PingOnceAsync(row.Ip).ConfigureAwait(true);
            _log($"[局域网] Ping {row.Ip} → {verdict}");
        }
        catch (Exception ex)
        {
            _log("[局域网] ❌ Ping 异常：" + ex.Message);
        }
    }

    [RelayCommand]
    private void DismissBanner() => BannerVisible = false;

    [RelayCommand]
    private void LocateBanner()
    {
        if (_lastResult?.ConflictedIps.FirstOrDefault() is string ip)
        {
            LocateRequested?.Invoke(ip);
        }
    }

    // ══════════════ 结果应用（赋值点手工通知，禁 partial 钩子） ══════════════

    private void ApplyResult(LanScanResult result, double? seconds = null, bool silent = false)
    {
        RebuildRows();
        Events.Clear();
        foreach (LanEvent evt in (_scan.PeekBaseline()?.Events ?? result.Events).Take(50))
        {
            Events.Add(new LanEventRow(evt));
        }

        DeviceCount = Rows.Count(r => r.Kind != LanRowKind.ConflictPeer);
        OnlineCount = result.Devices.Count;
        ConflictCount = result.ConflictedIps.Count;
        ChangedCount = result.Events.Count(e => e.Type == LanEventType.BindingChanged);
        NewCount = result.Events.Count(e => e.Type == LanEventType.NewDevice);
        OfflineCount = Rows.Count(r => r.Kind == LanRowKind.Offline);

        LastScanText = seconds is null
            ? $"上次扫描 {result.ScannedAt:HH:mm:ss}"
            : $"上次扫描 {result.ScannedAt:HH:mm:ss} · 耗时 {seconds:0.#}s";
        if (result.Truncated)
        {
            LastScanText += " · ⚠ 网段超上限已截断";
        }

        if (result.ConflictedIps.Count > 0)
        {
            LanEvent first = result.Events.First(e => e.Type == LanEventType.Conflict);
            BannerText = $"⚠ IP 冲突：{string.Join("、", result.ConflictedIps)} 正被多台设备应答";
            BannerDetail = first.Detail + "——疑似有人私改 IP";
            BannerVisible = true;
            if (!silent)
            {
                _log("[局域网] ⚠ " + BannerText);
            }
        }
        else
        {
            BannerVisible = false;
            if (!silent)
            {
                _log($"[局域网] ✅ 扫描完成：在线 {result.Devices.Count} 台，事件 {result.Events.Count} 条，无冲突");
            }
        }

        // 告警外呼（B3-③）：fire-and-forget——外呼绝不阻塞扫描主流程；
        // 未启用/未配 URL 时 PushAlertAsync 内部直接返回（零外呼路径）。
        _ = PushAlertAsync(result);
    }

    private LanSubnetPlan? BuildPlan(out string error)
    {
        error = string.Empty;
        if (SelectedAdapter is null)
        {
            error = "未选择网络适配器";
            return null;
        }

        string entry = FirstScannableEntry(SelectedAdapter.Model);
        if (entry.Length == 0)
        {
            error = "所选适配器无可用 IPv4（已排除 APIPA）";
            return null;
        }

        if (!LanSubnet.TryBuildFromAdapterEntry(entry, LanSubnet.DefaultHostLimit, out LanSubnetPlan? plan) || plan is null)
        {
            error = $"无法解析地址段 {entry}";
            return null;
        }

        // 本机 MAC 随计划下传（服务侧 ②b 特判补全用）
        return plan with { LocalMac = SelectedAdapter.Model.MacAddress };
    }

    /// <summary>可扫描判定：已连接物理网卡 + 有非 APIPA 的带前缀 IPv4（多值假设：逐条找，不做单值假设）。</summary>
    private static bool HasScannableIpv4(NetAdapterInfo adapter) =>
        adapter.Status == OperStatus.Up
        && adapter.Type is NetType.Ethernet or NetType.Wireless or NetType.Other
        && FirstScannableEntry(adapter).Length > 0;

    private static string FirstScannableEntry(NetAdapterInfo adapter) =>
        adapter.IPv4WithMask.FirstOrDefault(entry =>
            entry.Contains('/') && !entry.StartsWith("169.254.", StringComparison.Ordinal)) ?? string.Empty;
}
