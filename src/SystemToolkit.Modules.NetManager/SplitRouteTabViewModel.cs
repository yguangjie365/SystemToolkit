using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Core.Network.SplitRoute;

namespace SystemToolkit.Modules.NetManager;

/// <summary>分流候选网卡行（已连接 + 有网关才进候选——示意图 ③ 失败态判据同源）。</summary>
public sealed class SplitNicOption
{
    public SplitNicOption(NetAdapterInfo adapter, int ifIndex, int metric)
    {
        Adapter = adapter;
        IfIndex = ifIndex;
        Metric = metric;
    }

    public NetAdapterInfo Adapter { get; }

    public int IfIndex { get; }

    public int Metric { get; }

    public string Name => Adapter.Name;

    public string Gateway => Adapter.Gateways.FirstOrDefault() ?? "";

    public string Display => $"{Adapter.Name}（if {IfIndex} · gw {Gateway} · 跃点 {Metric}）";
}

/// <summary>台账路由展示行。</summary>
public sealed class SplitLedgerRow
{
    public SplitLedgerRow(SplitLedgerRoute route) => Route = route;

    public SplitLedgerRoute Route { get; }

    public string Prefix => Route.Prefix;

    public string Nexthop => Route.Nexthop;

    public string InterfaceName => Route.InterfaceName;

    public string KindText => Route.IsDefaultRoute ? "默认路由（外网）" : "内网段";
}

/// <summary>
/// 「分流路由」Tab（NET-7，示意图 2026-09-12 批准）：外网卡唯一默认路由 + 内网段逐条指路 +
/// 跃点调高；应用走 预览→确认→快照→执行→验证→失败自动回滚 全链（编排在 Core 侧
/// <see cref="SplitRouteService"/>，本 VM 只做状态与交互）。
/// <para>
/// 🔴 无 OnXxxChanged partial 钩子（_wpftmp CS0759 教训）；Selected* 参与判据走
/// [NotifyCanExecuteChangedFor]（CommandCanExecuteRefreshGuard）。
/// 示意图的「彻底模式（注册表根治）」已降级 V1.1（DisableDefaultRoute 无官方出处）——
/// 本 Tab 以「自动守护」开关承载同等效果。
/// </para>
/// </summary>
public partial class SplitRouteTabViewModel : ObservableObject
{
    /// <summary>守护巡检周期（DHCP 回潮清退；60s 量级——比参考项目的短时守护更持久）。</summary>
    public static readonly TimeSpan GuardInterval = TimeSpan.FromSeconds(60);

    private readonly INetworkInfoService _info;
    private readonly SplitRouteService _split;
    private readonly Action<string> _log;
    private CancellationTokenSource? _guardCts;

    public SplitRouteTabViewModel(INetworkInfoService info, SplitRouteService split, Action<string> log)
    {
        _info = info;
        _split = split;
        _log = log;
    }

    /// <summary>确认对话框回调（组合根转接，Repair 同款）。</summary>
    public Func<string, string, bool>? ConfirmRequest { get; set; }

    // ══════════════ 选择区 ══════════════

    public ObservableCollection<SplitNicOption> NicOptions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private SplitNicOption? _selectedWan;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private SplitNicOption? _selectedLan;

    /// <summary>内网网段清单（默认 RFC1918 三段，可增删——用户拍板 V1 保留为可删默认值）。</summary>
    public ObservableCollection<string> Cidrs { get; } = new(SplitCidr.DefaultPrivateCidrs);

    [ObservableProperty]
    private string _cidrInput = string.Empty;

    // ══════════════ 状态区 ══════════════

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _appliedText = "未应用";

    [ObservableProperty]
    private bool _isApplied;

    [ObservableProperty]
    private bool _isGuardOn;

    [ObservableProperty]
    private int _sweepCount;

    [ObservableProperty]
    private string _verifySummary = "应用后自动验证；也可随时手动重验。";

    /// <summary>重启自检黄条文本（空=不显示）：持久路由缺失时提示一键重应用。</summary>
    [ObservableProperty]
    private string _selfCheckWarning = string.Empty;

    [ObservableProperty]
    private string _selectionError = string.Empty;

    public ObservableCollection<SplitLedgerRow> LedgerRows { get; } = new();

    private bool CanApply => !IsBusy && SelectedWan is not null && SelectedLan is not null
        && !string.Equals(SelectedWan.Name, SelectedLan.Name, StringComparison.OrdinalIgnoreCase);

    // ══════════════ 加载 / 回显 ══════════════

    /// <summary>首载：候选网卡（已连接+有网关+netsh 索引可解析）+ 台账回显 + 重启自检。</summary>
    public async Task LoadAsync()
    {
        try
        {
            IReadOnlyList<NetAdapterInfo> adapters = await _info.GetAdaptersAsync().ConfigureAwait(true);
            var indexMap = (await _split.ReadInterfacesAsync().ConfigureAwait(true))
                .ToDictionary(static r => r.Name, StringComparer.OrdinalIgnoreCase);

            NicOptions.Clear();
            foreach (NetAdapterInfo adapter in adapters)
            {
                if (adapter.Status != OperStatus.Up || adapter.Gateways.Count == 0
                    || !indexMap.TryGetValue(adapter.Name, out RouteTableParser.InterfaceRow? row))
                {
                    continue;
                }

                NicOptions.Add(new SplitNicOption(adapter, row.IfIndex, row.Metric));
            }

            OnPropertyChanged(nameof(HasNoOptions));
            SelectionError = NicOptions.Count < 2
                ? "分流需要至少两块「已连接且有网关」的网卡（环回/未连接/APIPA 已排除）"
                : string.Empty;

            SplitLedger? ledger = _split.PeekLedger();
            if (ledger is not null)
            {
                SelectedWan = NicOptions.FirstOrDefault(o => string.Equals(o.Name, ledger.Wan.Name, StringComparison.OrdinalIgnoreCase));
                SelectedLan = NicOptions.FirstOrDefault(o => string.Equals(o.Name, ledger.Lan.Name, StringComparison.OrdinalIgnoreCase));
                Cidrs.Clear();
                foreach (SplitLedgerRoute route in ledger.Routes.Where(static r => !r.IsDefaultRoute))
                {
                    Cidrs.Add(route.Prefix);
                }

                RebuildLedgerRows(ledger);
                AppliedText = $"已应用 {ledger.AppliedAt:MM-dd HH:mm}";
                IsApplied = true;
                if (!IsGuardOn)
                {
                    IsGuardOn = true; // 台账在位默认起守护（防回潮是功能成立的前提）
                    StartGuardLoop();
                }

                IReadOnlyList<SplitLedgerRoute> missing = await _split.SelfCheckAsync().ConfigureAwait(true);
                if (missing.Count > 0)
                {
                    SelfCheckWarning = $"⚠ 重启自检：{missing.Count} 条持久路由缺失（{string.Join("、", missing.Select(static r => r.Prefix))}）——点「应用分流」一键补齐";
                    _log($"[分流] ⚠ 自检发现缺失 {missing.Count} 条");
                }
            }
            else if (NicOptions.Count >= 2)
            {
                // 默认预选：跃点低者作外网口（系统当前出口），另一块作内网口——用户可改
                var ordered = NicOptions.OrderBy(static o => o.Metric).ToList();
                SelectedWan ??= ordered[0];
                SelectedLan ??= ordered.Count > 1 ? ordered[1] : null;
            }
        }
        catch (Exception ex)
        {
            _log("[分流] ❌ 候选网卡读取异常：" + ex.Message);
        }
    }

    public bool HasNoOptions => NicOptions.Count < 2;

    // ══════════════ 命令 ══════════════

    [RelayCommand]
    private void AddCidr()
    {
        if (SplitCidr.Normalize(CidrInput) is not string cidr)
        {
            SelectionError = $"「{CidrInput}」不是合法 CIDR（如 10.0.0.0/8）";
            return;
        }

        if (!Cidrs.Contains(cidr))
        {
            Cidrs.Add(cidr);
        }

        CidrInput = string.Empty;
        SelectionError = string.Empty;
    }

    [RelayCommand]
    private void RemoveCidr(string? cidr)
    {
        if (cidr is not null)
        {
            Cidrs.Remove(cidr);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        SplitRequest? request = BuildRequest();
        if (request is null)
        {
            return;
        }

        IsBusy = true;
        ScanCommand_Notify();
        try
        {
            IReadOnlyList<SplitOp> preview = await _split.PreviewAsync(request).ConfigureAwait(true);
            string previewText = string.Join("\n", preview.Select((op, i) => $"{i + 1}. {op.Description}"));
            if (!Confirm("应用分流",
                $"将执行 {preview.Count} 项路由变更：\n\n{previewText}\n\n"
                + "⚠️ 应用瞬间默认路由切换，网络可能短暂中断（数秒）；远程机器请先确认带外恢复路径，"
                + "并暂时退出 VPN/代理。执行前自动保存配置快照，验证不过会自动回滚。\n\n确定继续吗？"))
            {
                _log("[分流] 已取消：用户未确认变更清单");
                return;
            }

            SelfCheckWarning = string.Empty;
            SplitApplyOutcome outcome = await _split.ApplyAsync(request, msg => { })
                .ConfigureAwait(true);
            foreach (string line in outcome.Message.Split('\n'))
            {
                _log("[分流] " + line);
            }

            if (outcome.Success)
            {
                SplitLedger? ledger = _split.PeekLedger();
                if (ledger is not null)
                {
                    RebuildLedgerRows(ledger);
                    AppliedText = $"已应用 {ledger.AppliedAt:HH:mm:ss}";
                }

                IsApplied = true;
                VerifySummary = FormatVerify(outcome.Verification);
                if (!IsGuardOn)
                {
                    IsGuardOn = true;
                    StartGuardLoop();
                }

                _log("[分流] ✅ 分流已应用");
            }
            else
            {
                VerifySummary = outcome.RolledBack
                    ? "❌ 应用失败/验证未过——已自动回滚到应用前状态：" + outcome.Message
                    : "❌ " + outcome.Message;
            }
        }
        catch (Exception ex)
        {
            // 审查 🟠-2 同款：AsyncRelayCommand 吞异常，必须用户可见
            _log("[分流] ❌ 应用异常：" + ex.Message);
            VerifySummary = "❌ 应用异常：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ScanCommand_Notify();
        }
    }

    [RelayCommand]
    private async Task ReverifyAsync()
    {
        SplitRequest? request = BuildRequest();
        if (request is null)
        {
            return;
        }

        try
        {
            SplitVerifyReport report = await _split.VerifyAsync(request).ConfigureAwait(true);
            VerifySummary = FormatVerify(report);
        }
        catch (Exception ex)
        {
            VerifySummary = "❌ 验证异常：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!Confirm("恢复原路由",
                "将按台账删除本工具创建的全部路由、内网卡跃点回原值，并对外网卡执行 DHCP 续租重建系统默认路由。\n\n确定恢复吗？"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            StopGuardLoop();
            IsGuardOn = false;
            bool ok = await _split.RestoreAsync(msg => _log("[分流] " + msg)).ConfigureAwait(true);
            if (ok)
            {
                LedgerRows.Clear();
                IsApplied = false;
                AppliedText = "未应用";
                SweepCount = 0;
                VerifySummary = "已恢复系统默认路由态。";
                SelfCheckWarning = string.Empty;
            }
        }
        catch (Exception ex)
        {
            _log("[分流] ❌ 恢复异常：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>守护开关翻转（ToggleButton Command 通道，LanScan 监控同款）。</summary>
    [RelayCommand]
    private void ToggleGuard()
    {
        if (IsGuardOn)
        {
            StartGuardLoop();
            _log($"[分流] 守护巡检已开启（每 {GuardInterval.TotalSeconds:0}s 查一次内网卡回潮）");
        }
        else
        {
            StopGuardLoop();
            _log("[分流] 守护巡检已关闭——DHCP 续租可能悄悄加回内网默认路由（分流失效头号原因）");
        }
    }

    /// <summary>页面卸载收口（O7 同款：切页/关窗必停后台循环）。</summary>
    public void CancelGuard()
    {
        StopGuardLoop();
        if (IsGuardOn)
        {
            IsGuardOn = false;
        }
    }

    // ══════════════ 内部 ══════════════

    private SplitRequest? BuildRequest()
    {
        if (SelectedWan is null || SelectedLan is null)
        {
            SelectionError = "请先选择外网口与内网口";
            return null;
        }

        if (string.Equals(SelectedWan.Name, SelectedLan.Name, StringComparison.OrdinalIgnoreCase))
        {
            SelectionError = "外网口与内网口不能相同";
            return null;
        }

        if (Cidrs.Count == 0)
        {
            SelectionError = "内网网段不能为空";
            return null;
        }

        SelectionError = string.Empty;
        return new SplitRequest(
            new SplitNicSelection(SelectedWan.Name, SelectedWan.IfIndex, SelectedWan.Gateway),
            new SplitNicSelection(SelectedLan.Name, SelectedLan.IfIndex, SelectedLan.Gateway),
            Cidrs.ToList());
    }

    private void RebuildLedgerRows(SplitLedger ledger)
    {
        LedgerRows.Clear();
        foreach (SplitLedgerRoute route in ledger.Routes)
        {
            LedgerRows.Add(new SplitLedgerRow(route));
        }
    }

    private static string FormatVerify(SplitVerifyReport? v) => v is null
        ? "—"
        : (v.AllPassed
            ? $"✅ 默认路由唯一走外网口 · 外网 {v.WanAvgMs}ms · 内网 {v.LanAvgMs}ms"
            : "❌ " + string.Join("；", v.Problems));

    private void StartGuardLoop()
    {
        StopGuardLoop();
        _guardCts = new CancellationTokenSource();
        CancellationToken ct = _guardCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _split.RunGuardLoopAsync(GuardInterval, async swept =>
                {
                    SweepCount += swept;
                    _log($"[分流] 守护：清退内网卡回潮默认路由 {swept} 条（累计 {SweepCount}）");
                    await Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log("[分流] ❌ 守护循环异常终止：" + ex.Message);
            }
        }, CancellationToken.None);
    }

    private void StopGuardLoop()
    {
        _guardCts?.Cancel();
        _guardCts = null;
    }

    private bool Confirm(string title, string message)
        => ConfirmRequest?.Invoke(title, message) == true; // 审查 Y1：确认缺省拒绝（fail-closed）

    private void ScanCommand_Notify() => ApplyCommand.NotifyCanExecuteChanged();
}
