using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Network.LanScan;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 「局域网扫描」Tab（NET-6）：主动扫段 + 基线 diff + IP 冲突监控。布局契约见已批准示意图
/// <c>Docs/mockups/局域网扫描-NET6示意图.html</c>（①主界面 ②空态 ③失败态）。
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
    private CancellationTokenSource? _monitorCts;
    private LanScanResult? _lastResult;
    private string _filter = string.Empty;

    public LanScanTabViewModel(INetworkInfoService info, LanScanService scan, Action<string> log)
    {
        _info = info;
        _scan = scan;
        _log = log;
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

    /// <summary>从未扫描过时展示既有基线设备（灰/离线形态），避免首屏全空丢历史。</summary>
    private void LoadExistingBaseline()
    {
        LanBaseline? baseline = _scan.PeekBaseline();
        if (baseline is null || baseline.Entries.Count == 0)
        {
            return;
        }

        _log($"[局域网] 已加载历史基线 {baseline.Entries.Count} 台（来自上次运行），点「重新扫描」刷新");
        Events.Clear();
        foreach (LanEvent evt in baseline.Events.Take(50))
        {
            Events.Add(new LanEventRow(evt));
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
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
            LanScanResult result = await _scan.ScanAsync(plan, progress, runCts.Token)
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
            _runCts = null;
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
            _monitorCts = new CancellationTokenSource();
            _log($"[局域网] 自动监控已开启（每 {MonitorInterval.TotalMinutes:0} 分钟一轮，可取消）");
            _ = MonitorLoopAsync(_monitorCts.Token);
        }
        else
        {
            _monitorCts?.Cancel();
            _monitorCts = null;
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
                    LanScanResult result = await _scan.ScanAsync(plan, null, ct).ConfigureAwait(true);
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

    /// <summary>页面卸载/切走时停监控（Diagnostics.CancelPing 同款纪律，审查 O7）。</summary>
    public void CancelMonitor()
    {
        if (!IsMonitorOn)
        {
            return;
        }

        IsMonitorOn = false;
        _monitorCts?.Cancel();
        _monitorCts = null;
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

    [RelayCommand]
    private void PingRow(LanDeviceRow? row)
    {
        if (row is not null)
        {
            PingRequested?.Invoke(row.Ip);
        }
    }

    /// <summary>Ping 联动请求（组合根转接：目标写入诊断页持续 ping 并切 Tab）。</summary>
    public event Action<string>? PingRequested;

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

        return plan;
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
