using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Modules.NetManager;

/// <summary>适配器列表行投影 VM（列表范式：多网卡红线——全部展示并标注类型，不做单值假设）。</summary>
public sealed class AdapterRowVm
{
    public NetAdapterInfo Model { get; }

    public AdapterRowVm(NetAdapterInfo model) => Model = model;

    public string Name => Model.Name;

    /// <summary>下拉/列表主文本：名称（类型，连接态）。</summary>
    public string DisplayText => $"{Model.Name}（{TypeText} · {StatusText}）";

    public string TypeText => Model.Type switch
    {
        NetType.Ethernet => "以太网",
        NetType.Wireless => "无线",
        NetType.Other => "虚拟/其他",
        _ => Model.Type.ToString(),
    };

    public string StatusText => Model.Status == OperStatus.Up ? "已连接" : "已断开";

    public bool IsUp => Model.Status == OperStatus.Up;

    public string SpeedText => Model.SpeedMbps switch
    {
        0 => "—",
        >= 1000 => $"{Model.SpeedMbps / 1000.0:0.#} Gbps",
        _ => $"{Model.SpeedMbps} Mbps",
    };

    public string MacText => string.IsNullOrEmpty(Model.MacAddress) ? "—" : Model.MacAddress;

    public string DhcpText => Model.IsDhcp ? "自动获取（DHCP）" : "手动（静态）";

    public string IpText => Model.IPv4WithMask.Count > 0 ? string.Join("、", Model.IPv4WithMask) : "—";

    public string GatewayText => Model.Gateways.Count > 0 ? string.Join("、", Model.Gateways) : "—";

    public string DnsText => Model.DnsServers.Count > 0 ? string.Join("、", Model.DnsServers) : "自动获取";
}

/// <summary>
/// 「网络设置」Tab：网卡列表/详情 + IPv4（DHCP/静态）+ DNS 预设 + 系统代理 + 配置快照。
/// 所有写操作 = 校验 → 确认 → 自动快照 → 执行 → 回读刷新（设计 04 §5 修改类操作四步纪律）。
/// </summary>
public partial class NetSettingsTabViewModel : ObservableObject
{
    private readonly INetworkInfoService _info;
    private readonly INetConfigService _config;
    private readonly INetworkSnapshotService _snapshots;
    private readonly Action<string> _log;

    public NetSettingsTabViewModel(
        INetworkInfoService info,
        INetConfigService config,
        INetworkSnapshotService snapshots,
        Action<string> log)
    {
        _info = info;
        _config = config;
        _snapshots = snapshots;
        _log = log;
    }

    /// <summary>确认对话框回调（由组合根转接）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    public ObservableCollection<AdapterRowVm> Adapters { get; } = new();

    [ObservableProperty]
    private AdapterRowVm? _selectedAdapter;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoaded;

    // ── IPv4 表单 ──
    [ObservableProperty]
    private bool _ipModeStatic;

    [ObservableProperty]
    private string _ipInput = "";

    [ObservableProperty]
    private string _maskInput = "";

    [ObservableProperty]
    private string _gatewayInput = "";

    [ObservableProperty]
    private string _ipFormError = "";

    // ── DNS 表单 ──
    public IReadOnlyList<DnsPreset> PresetList => DnsPresets.BuiltIn;

    [ObservableProperty]
    private DnsPreset? _selectedPreset;

    [ObservableProperty]
    private string _dnsPrimaryInput = "";

    [ObservableProperty]
    private string _dnsSecondaryInput = "";

    [ObservableProperty]
    private string _dnsFormError = "";

    // ── 代理表单 ──
    [ObservableProperty]
    private bool _proxyEnabled;

    [ObservableProperty]
    private string _proxyServerInput = "";

    [ObservableProperty]
    private string _proxyFormError = "";

    // ── 快照 ──
    public ObservableCollection<NetworkSnapshotRecord> Snapshots { get; } = new();

    [ObservableProperty]
    private NetworkSnapshotRecord? _selectedSnapshot;

    partial void OnSelectedAdapterChanged(AdapterRowVm? value)
    {
        if (value is null)
        {
            return;
        }

        // 选中即回填 IPv4 表单（DHCP 模式不回填旧静态值——老工程踩坑：掩码不可回读）
        IpModeStatic = !value.Model.IsDhcp;
        IpFormError = "";
        string primary = value.Model.IPv4WithMask.Count > 0 ? value.Model.IPv4WithMask[0] : "";
        int slash = primary.IndexOf('/');
        IpInput = value.Model.IsDhcp || slash <= 0 ? "" : primary[..slash];
        MaskInput = "";
        GatewayInput = value.Model.Gateways.Count > 0 ? value.Model.Gateways[0] : "";
    }

    partial void OnSelectedPresetChanged(DnsPreset? value)
    {
        if (value is null)
        {
            return;
        }

        DnsPrimaryInput = value.Primary ?? "";
        DnsSecondaryInput = value.Secondary ?? "";
        DnsFormError = "";
    }

    // ── 加载 ──

    [RelayCommand(CanExecute = nameof(CanLoad))]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<NetAdapterInfo> adapters = await _info.GetAdaptersAsync().ConfigureAwait(true);
            Adapters.Clear();
            foreach (NetAdapterInfo adapter in adapters)
            {
                Adapters.Add(new AdapterRowVm(adapter));
            }

            if (SelectedAdapter is null && Adapters.Count > 0)
            {
                SelectedAdapter = Adapters.FirstOrDefault(a => a.IsUp) ?? Adapters[0];
            }

            ProxyInfo proxy = _info.GetSystemProxy();
            ProxyEnabled = proxy.Enabled;
            ProxyServerInput = proxy.Server ?? "";

            await RefreshSnapshotsAsync().ConfigureAwait(true);
            IsLoaded = true;
            _log($"[设置] 已加载 {Adapters.Count} 个适配器、{Snapshots.Count} 份配置快照");
        }
        catch (Exception ex)
        {
            _log("[设置] ❌ 适配器加载失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLoad => !IsBusy;

    private async Task RefreshSnapshotsAsync()
    {
        Snapshots.Clear();
        foreach (NetworkSnapshotRecord record in await _snapshots.ListAsync().ConfigureAwait(true))
        {
            Snapshots.Add(record);
        }
    }

    // ── IPv4 应用 / 回退 DHCP ──

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ApplyIpAsync()
    {
        IpFormError = "";
        if (SelectedAdapter is null)
        {
            IpFormError = "请先选择网卡";
            return;
        }

        string adapter = SelectedAdapter.Name;
        if (!IpModeStatic)
        {
            // 切回 DHCP：同样先快照再执行
            if (!Confirm("恢复 DHCP", $"将把「{adapter}」的 IPv4 改回自动获取。\n确定继续吗？"))
            {
                return;
            }

            await SnapshotThenRunAsync("RestoreDhcp", async () =>
                await _config.SetDhcpAsync(adapter, _log).ConfigureAwait(true)).ConfigureAwait(true);
            return;
        }

        // 静态：三值全量校验（执行前拦截，零进程启动）
        if (!IpValidation.IsIPv4(IpInput))
        {
            IpFormError = "IP 必须是四段合法 IPv4";
            return;
        }

        if (!IpValidation.IsIPv4(MaskInput))
        {
            IpFormError = "子网掩码必须是合法 IPv4（如 255.255.255.0）";
            return;
        }

        string? gateway = string.IsNullOrWhiteSpace(GatewayInput) ? null : GatewayInput;
        if (gateway is not null && !IpValidation.IsIPv4(gateway))
        {
            IpFormError = "网关格式非法（可留空表示无网关）";
            return;
        }

        string oldText = SelectedAdapter.IpText;
        if (!Confirm("配置静态 IP",
                $"网卡「{adapter}」\n{oldText}（{SelectedAdapter.DhcpText}）\n→ {IpInput} / {MaskInput}"
                + (gateway is null ? "（无网关）" : $" / 网关 {gateway}")
                + "\n\n执行前将自动保存配置快照，失败可一键回滚。\n确定继续吗？"))
        {
            return;
        }

        await SnapshotThenRunAsync("SetStaticIp", async () =>
            await _config.SetStaticIpAsync(adapter, IpInput, MaskInput, gateway, _log).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ToggleAdapterAsync()
    {
        if (SelectedAdapter is null)
        {
            return;
        }

        bool disable = SelectedAdapter.IsUp; // Up → 禁用；Down → 启用
        string action = disable ? "禁用" : "启用";
        if (!Confirm($"{action}网卡", $"确定{action}「{SelectedAdapter.Name}」吗？"
                + (disable ? "\n\n⚠️ 若这是当前连接，网络会立即中断。" : "")))
        {
            return;
        }

        string adapter = SelectedAdapter.Name;
        IsBusy = true;
        try
        {
            int exit = await _config.SetAdapterEnabledAsync(adapter, enabled: !disable, _log).ConfigureAwait(true);
            // 审查 O6（2026-09-10）：1223=用户拒绝 UAC=安全终止（对齐修复页纪律）
            _log(exit == 0
                ? $"[设置] ✅ 已{action}「{adapter}」"
                : exit == 1223
                    ? $"[设置] ⚠️ {action}「{adapter}」：用户拒绝 UAC 提权，已安全终止（无副作用）"
                    : $"[设置] ❌ {action}「{adapter}」失败（退出码 {exit}）");
            await Task.Delay(1500).ConfigureAwait(true); // 状态切换落地等待，再回读
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── DNS ──

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ApplyDnsAsync()
    {
        DnsFormError = "";
        if (SelectedAdapter is null)
        {
            DnsFormError = "请先选择网卡";
            return;
        }

        string? primary = string.IsNullOrWhiteSpace(DnsPrimaryInput) ? null : DnsPrimaryInput;
        string? secondary = string.IsNullOrWhiteSpace(DnsSecondaryInput) ? null : DnsSecondaryInput;
        if (primary is null && secondary is not null)
        {
            DnsFormError = "填写备用 DNS 前必须先填主 DNS";
            return;
        }

        if (primary is not null && !IpValidation.IsIPv4(primary))
        {
            DnsFormError = "主 DNS 必须是合法 IPv4";
            return;
        }

        if (secondary is not null && !IpValidation.IsIPv4(secondary))
        {
            DnsFormError = "备用 DNS 必须是合法 IPv4";
            return;
        }

        string adapter = SelectedAdapter.Name;
        string desc = primary is null ? "恢复自动获取（DHCP）" : $"{primary}" + (secondary is null ? "" : $" / {secondary}");
        if (!Confirm("设置 DNS", $"网卡「{adapter}」DNS → {desc}\n执行前将自动保存配置快照。\n确定继续吗？"))
        {
            return;
        }

        await SnapshotThenRunAsync("SetDns", async () =>
            await _config.SetDnsAsync(adapter, primary, secondary, _log).ConfigureAwait(true)).ConfigureAwait(true);
    }

    // ── 代理 ──

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ApplyProxyAsync()
    {
        ProxyFormError = "";
        string? server = string.IsNullOrWhiteSpace(ProxyServerInput) ? null : ProxyServerInput;
        if (ProxyEnabled)
        {
            if (server is null || !IpValidation.IsProxyServer(server))
            {
                ProxyFormError = "代理服务器格式必须是 host:port（端口 1-65535）";
                return;
            }
        }

        string desc = ProxyEnabled ? $"启用，服务器 {server}" : "禁用";
        if (!Confirm("系统代理", $"系统代理 → {desc}（写注册表 + WinINET 刷新通知）\n确定继续吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _snapshots.CaptureAsync(
                NetworkSnapshotService.ReasonBeforeChange, relatedAction: "SetProxy",
                onLine: _log).ConfigureAwait(true);
            _info.SetSystemProxy(ProxyEnabled, server, _log);
            _log("[设置] ✅ 系统代理已更新");
        }
        catch (Exception ex)
        {
            _log("[设置] ❌ 代理写入失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 快照管理 ──

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RestoreSnapshotAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        NetworkSnapshotRecord record = SelectedSnapshot;
        if (!Confirm("恢复配置快照",
                $"将把网络配置恢复到 {record.CreatedAt:yyyy-MM-dd HH:mm:ss} 的快照：\n{record.Name}\n\n"
                + "恢复范围：适配器 IPv4/DNS → TCP 调优 → 接口跃点数 → 系统代理。\n"
                + "恢复前会自动再拍一份当前状态快照（可撤销本次恢复）。\n确定继续吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _snapshots.RestoreAsync(record.Id, _log).ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            _log("[快照] ❌ " + ex.Message);
        }
        catch (Exception ex)
        {
            _log("[快照] ❌ 恢复失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSnapshotAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        NetworkSnapshotRecord record = SelectedSnapshot;
        if (!Confirm("删除快照", $"确定删除 {record.CreatedAt:yyyy-MM-dd HH:mm:ss} 的快照吗？此操作不可撤销。"))
        {
            return;
        }

        await _snapshots.DeleteAsync(record.Id).ConfigureAwait(true);
        _log($"[快照] 已删除 {record.CreatedAt:yyyy-MM-dd HH:mm:ss} 的快照");
        await RefreshSnapshotsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshSnapshotsCommandAsync() => await RefreshSnapshotsAsync().ConfigureAwait(true);

    // ── 公共 ──

    private bool CanWrite => !IsBusy;

    private bool Confirm(string title, string message)
        => ConfirmRequest?.Invoke(title, message) == true; // 审查 Y1：危险操作确认缺省应拒绝（fail-closed）

    /// <summary>修改类操作统一通道：先拍 BeforeChange 快照 → 执行 → 按退出码报结果 → 回读刷新。</summary>
    private async Task SnapshotThenRunAsync(string relatedAction, Func<Task<int>> run)
    {
        IsBusy = true;
        try
        {
            await _snapshots.CaptureAsync(
                NetworkSnapshotService.ReasonBeforeChange, relatedAction: relatedAction,
                onLine: _log).ConfigureAwait(true);

            int exit = await run().ConfigureAwait(true);
            // 审查 O6（2026-09-10）：同上，1223 显式识别
            _log(exit == 0
                ? $"[设置] ✅ {relatedAction} 已执行，正在回读验证……"
                : exit == 1223
                    ? $"[设置] ⚠️ {relatedAction}：用户拒绝 UAC 提权，已安全终止（可用配置快照回滚）"
                    : $"[设置] ❌ {relatedAction} 失败（退出码 {exit}）。可用配置快照一键回滚");
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
