using System.Runtime.Versioning;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Core.Network.SplitRoute;

/// <summary>
/// 双网卡分流编排（NET-7 心脏）：预览 → 快照 → 执行 → 验证 → 失败自动回滚（AGENTS 修改类四步纪律）。
/// <para>
/// 核心三件事（调研定稿，两个 MIT/无许可证参考项目的共识）：外网卡持有<b>唯一</b>默认路由；
/// 内网段逐条 route 指到内网网关（最长前缀优先天然分流）；内网卡接口跃点调高压过外网口。
/// 命令面全部 <see cref="NetshArgs"/> 构造 + <see cref="ICommandRunner"/>（写命令经
/// ElevatingCommandRunner 白名单按需 UAC）；删除只按路由表实测行与台账四要素精确匹配，
/// 绝不按前缀模糊清理——误删系统路由是这类工具的头号事故源。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SplitRouteService
{
    /// <summary>操作边界动作名（06 册 §4 登记）。</summary>
    public const string ApplyAction = "SplitApply";

    /// <summary>恢复动作名。</summary>
    public const string RestoreAction = "SplitRestore";

    /// <summary>守护清退动作名（仅实际清退时落）。</summary>
    public const string GuardAction = "SplitGuard";

    /// <summary>外网连通判据探测点（阿里 DNS，国内免污染可达）。</summary>
    public const string ExternalProbeHost = "223.5.5.5";

    private readonly ICommandRunner _runner;
    private readonly INetProbe _probe;
    private readonly SplitLedgerStore _ledgerStore;
    private readonly INetworkSnapshotService _snapshots;
    private readonly ILogger _log;

    /// <summary>runner 注入 <c>ElevatingCommandRunner</c>（写命令按需 UAC）；probe 复用诊断页同源实现。</summary>
    public SplitRouteService(
        ICommandRunner runner,
        INetProbe probe,
        SplitLedgerStore ledgerStore,
        INetworkSnapshotService snapshots,
        ILogger? logger = null)
    {
        _runner = runner;
        _probe = probe;
        _ledgerStore = ledgerStore;
        _snapshots = snapshots;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>是否已有分流在应用态（台账存在即视为生效中，UI 决定按钮组形态）。</summary>
    public bool HasActiveLedger => _ledgerStore.Load().State == SplitLedgerLoadStatus.Ok;

    /// <summary>
    /// 生成变更清单（预览所见即执行所得）。删除项全部来自路由表实测行——不存在的行不生成删除命令
    /// （netsh delete 未命中会返非零，整步失败）。
    /// </summary>
    public async Task<IReadOnlyList<SplitOp>> PreviewAsync(SplitRequest request, CancellationToken ct = default)
    {
        Validate(request);
        IReadOnlyList<RouteRow> table = await ReadRouteTableAsync(ct).ConfigureAwait(false);
        int lanOrigMetric = await ReadLanInterfaceMetricAsync(request.Lan.Name, ct).ConfigureAwait(false);
        return BuildOps(request, table, lanOrigMetric).ops;
    }

    /// <summary>应用分流：快照 → 逐条执行 → 三判据验证；任何失败自动回滚已做的变更。</summary>
    public async Task<SplitApplyOutcome> ApplyAsync(
        SplitRequest request, Action<string> onLine, CancellationToken ct = default)
    {
        using LogTiming timing = _log.Time(ApplyAction);
        var applied = new List<SplitOp>();
        try
        {
            if (HasActiveLedger)
            {
                timing.Complete(LogResult.Rejected, LogLevel.Warn, $"{ApplyAction} 拒绝：已有分流在应用态");
                return new SplitApplyOutcome(false, false, "已有分流在应用态——请先「恢复原路由」再改配置");
            }

            Validate(request);
            IReadOnlyList<RouteRow> table = await ReadRouteTableAsync(ct).ConfigureAwait(false);
            int lanOrigMetric = await ReadLanInterfaceMetricAsync(request.Lan.Name, ct).ConfigureAwait(false);
            (List<SplitOp> ops, _) = BuildOps(request, table, lanOrigMetric);

            onLine($"[分流] 计划变更 {ops.Count} 项；先存全量配置快照（可整组回退）……");
            await _snapshots.CaptureAsync(
                NetworkSnapshotService.ReasonBeforeChange, ApplyAction, onLine: onLine, ct: ct)
                .ConfigureAwait(false);

            foreach (SplitOp op in ops)
            {
                onLine($"  $ {op.FileName} {op.Arguments}　—— {op.Description}");
                int exit = await _runner.RunAsync(op.FileName, op.Arguments, onLine, ct).ConfigureAwait(false);
                if (exit != 0)
                {
                    onLine($"[分流] ❌ 第 {ops.IndexOf(op) + 1} 步失败（退出码 {exit}），自动回滚已执行部分……");
                    await RollbackAsync(applied, request, lanOrigMetric, onLine, ct).ConfigureAwait(false);
                    timing.Complete(LogResult.Failed, LogLevel.Error,
                        $"{ApplyAction} 失败已回滚：{op.Description}");
                    return new SplitApplyOutcome(false, true, $"执行中断于「{op.Description}」，已自动回滚（退出码 {exit}）");
                }

                applied.Add(op);
            }

            SplitVerifyReport verify = await VerifyAsync(request, ct).ConfigureAwait(false);
            ReportVerify(verify, onLine);
            if (!verify.AllPassed)
            {
                onLine("[分流] ❌ 验证未通过，自动回滚到应用前状态……");
                await RollbackAsync(applied, request, lanOrigMetric, onLine, ct).ConfigureAwait(false);
                timing.Complete(LogResult.Failed, LogLevel.Error, $"{ApplyAction} 验证失败已回滚");
                return new SplitApplyOutcome(false, true, "验证未通过，已自动回滚：" + string.Join("；", verify.Problems), verify);
            }

            _ledgerStore.Save(new SplitLedger(
                SplitLedgerStore.FormatVersion,
                DateTimeOffset.Now,
                request.Wan,
                request.Lan,
                lanOrigMetric,
                LedgerRoutes(request)));
            onLine("[分流] ✅ 已应用并验证通过；守护循环可开启（清退 DHCP 回潮的默认路由）");
            timing.Complete(message: $"{ApplyAction} 完成：{applied.Count} 项变更，台账已落");
            return new SplitApplyOutcome(true, false, "分流已应用", verify);
        }
        catch (OperationCanceledException)
        {
            onLine("[分流] 已取消——回滚已执行部分……");
            await RollbackAsync(applied, request, 0, onLine, CancellationToken.None).ConfigureAwait(false);
            timing.Complete(LogResult.Cancelled, LogLevel.Warn, $"{ApplyAction} 取消（已尽力回滚）");
            return new SplitApplyOutcome(false, true, "已取消并回滚（内网卡跃点恢复以「恢复原路由」为准）");
        }
        catch (Exception ex)
        {
            await RollbackAsync(applied, request, 0, onLine, CancellationToken.None).ConfigureAwait(false);
            timing.Complete(LogResult.Failed, LogLevel.Error, $"{ApplyAction} 异常（已尽力回滚）", ex);
            return new SplitApplyOutcome(false, true, "异常已回滚：" + ex.Message);
        }
    }

    /// <summary>恢复原路由：按台账逐条精确删除 + 内网卡跃点回原值 + 外网卡 DHCP 续租重建系统默认路由。</summary>
    public async Task<bool> RestoreAsync(Action<string> onLine, CancellationToken ct = default)
    {
        using LogTiming timing = _log.Time(RestoreAction);
        (SplitLedgerLoadStatus State, SplitLedger? Ledger) = _ledgerStore.Load();
        if (State != SplitLedgerLoadStatus.Ok || Ledger is null)
        {
            onLine(State == SplitLedgerLoadStatus.Corrupted
                ? "[分流] ❌ 台账损坏，无法安全恢复——请手动核对路由表（netsh interface ipv4 show route）"
                : "[分流] 当前没有已应用的分流");
            timing.Complete(LogResult.Rejected, LogLevel.Warn, $"{RestoreAction} 无可恢复台账（state={State}）");
            return false;
        }

        try
        {
            foreach (SplitLedgerRoute route in Ledger.Routes)
            {
                onLine($"  $ netsh {NetshArgs.DeleteRoute(route.InterfaceName, route.Prefix, route.Nexthop)}");
                await _runner.RunAsync("netsh",
                    NetshArgs.DeleteRoute(route.InterfaceName, route.Prefix, route.Nexthop),
                    onLine, ct).ConfigureAwait(false); // 个别删除失败不阻断——最后统一以快照恢复页为准
            }

            onLine($"  $ netsh {NetshArgs.SetInterfaceMetric(Ledger.Lan.Name, Ledger.LanOriginalInterfaceMetric)}");
            await _runner.RunAsync("netsh",
                NetshArgs.SetInterfaceMetric(Ledger.Lan.Name, Ledger.LanOriginalInterfaceMetric),
                onLine, ct).ConfigureAwait(false);

            onLine($"  $ ipconfig {NetshArgs.Renew(Ledger.Wan.Name)}　—— 续租重建外网默认路由");
            await _runner.RunAsync("ipconfig", NetshArgs.Renew(Ledger.Wan.Name), onLine, ct).ConfigureAwait(false);

            _ledgerStore.Clear();
            onLine("[分流] ✅ 已恢复到应用前状态（台账已清除）");
            timing.Complete(message: $"{RestoreAction} 完成：{Ledger.Routes.Count} 条路由回退");
            return true;
        }
        catch (Exception ex)
        {
            timing.Complete(LogResult.Failed, LogLevel.Error, $"{RestoreAction} 异常", ex);
            throw;
        }
    }

    // ── 内部：计划构建 / 回滚 / 读取 ──

    private static void Validate(SplitRequest request)
    {
        if (string.Equals(request.Wan.Name, request.Lan.Name, StringComparison.OrdinalIgnoreCase)
            || request.Wan.IfIndex == request.Lan.IfIndex)
        {
            throw new ArgumentException("外网口与内网口不能是同一块网卡");
        }

        if (request.Wan.Gateway.Length == 0 || request.Lan.Gateway.Length == 0)
        {
            throw new ArgumentException("两口都必须已有网关才谈分流");
        }

        foreach (string cidr in request.Cidrs)
        {
            if (SplitCidr.Normalize(cidr) is null)
            {
                throw new ArgumentException($"非法 CIDR：{cidr}");
            }
        }
    }

    private (List<SplitOp> ops, int lanOrigMetric) BuildOps(
        SplitRequest request, IReadOnlyList<RouteRow> table, int lanOrigMetric)
    {
        var ops = new List<SplitOp>();

        // ① 内网卡默认路由全部摘除（分流的前提；来源=路由表实测行，绝不盲删）
        foreach (RouteRow row in table.Where(r => r.IsDefault && r.IfIndex == request.Lan.IfIndex))
        {
            ops.Add(new SplitOp(SplitOpKind.DeleteRoute,
                $"移除内网卡默认路由（→ {row.Nexthop}）",
                "netsh", NetshArgs.DeleteRoute(request.Lan.Name, "0.0.0.0/0", row.Nexthop)));
        }

        // ② 外网卡既有默认路由先删后加重写为持久（清掉临时/重复条目，跃点归一）
        foreach (RouteRow row in table.Where(r => r.IsDefault && r.IfIndex == request.Wan.IfIndex))
        {
            ops.Add(new SplitOp(SplitOpKind.DeleteRoute,
                $"清理外网卡原默认路由（→ {row.Nexthop}）",
                "netsh", NetshArgs.DeleteRoute(request.Wan.Name, "0.0.0.0/0", row.Nexthop)));
        }

        ops.Add(new SplitOp(SplitOpKind.AddRoute,
            $"外网卡唯一默认路由 → {request.Wan.Gateway}（持久，跃点 {request.WanDefaultMetric}）",
            "netsh", NetshArgs.AddRoute(request.Wan.Name, "0.0.0.0/0", request.Wan.Gateway,
                persistent: true, request.WanDefaultMetric)));

        // ③ 内网段逐条指路（最长前缀优先天然走内网卡）
        foreach (string raw in request.Cidrs)
        {
            string cidr = SplitCidr.Normalize(raw)!;
            ops.Add(new SplitOp(SplitOpKind.AddRoute,
                $"内网段 {cidr} → {request.Lan.Gateway}（持久，跃点 {request.LanRouteMetric}）",
                "netsh", NetshArgs.AddRoute(request.Lan.Name, cidr, request.Lan.Gateway,
                    persistent: true, request.LanRouteMetric)));
        }

        // ④ 内网卡接口跃点调高（原值进台账，恢复回退）
        if (lanOrigMetric != request.LanInterfaceMetric)
        {
            ops.Add(new SplitOp(SplitOpKind.SetInterfaceMetric,
                $"内网卡跃点 {lanOrigMetric} → {request.LanInterfaceMetric}（压过自动叠加，防回程走错口）",
                "netsh", NetshArgs.SetInterfaceMetric(request.Lan.Name, request.LanInterfaceMetric)));
        }

        return (ops, lanOrigMetric);
    }

    /// <summary>回滚=逆序删除已加的路由/跃点回原值 + 外网卡续租重建默认路由（尽力而为，全程吞单步失败）。</summary>
    private async Task RollbackAsync(
        List<SplitOp> applied, SplitRequest request, int lanOrigMetric, Action<string> onLine, CancellationToken ct)
    {
        foreach (SplitOp op in applied.AsEnumerable().Reverse())
        {
            if (op.Kind == SplitOpKind.AddRoute && op.Arguments.Contains("prefix=", StringComparison.Ordinal))
            {
                (string iface, string prefix, string nexthop) = ParseRouteArgs(op.Arguments);
                await _runner.RunAsync("netsh", NetshArgs.DeleteRoute(iface, prefix, nexthop), _ => { }, ct)
                    .ConfigureAwait(false);
            }
        }

        // lanOrigMetric=0 表示原为自动跃点态/取消路径未采到——跳过跃点回写（0 非法值白名单必拒）
        if (lanOrigMetric > 0 && applied.Any(static o => o.Kind == SplitOpKind.SetInterfaceMetric))
        {
            await _runner.RunAsync("netsh", NetshArgs.SetInterfaceMetric(request.Lan.Name, lanOrigMetric),
                _ => { }, ct).ConfigureAwait(false);
        }

        // 外网卡默认路由若被我们重写/删除过 → 续租让 DHCP 重建系统默认路由
        if (applied.Any(static o => o.Kind is SplitOpKind.AddRoute or SplitOpKind.DeleteRoute))
        {
            await _runner.RunAsync("ipconfig", NetshArgs.Renew(request.Wan.Name), _ => { }, ct)
                .ConfigureAwait(false);
        }

        onLine("[分流] 回滚执行完毕");
    }

    private static (string Iface, string Prefix, string Nexthop) ParseRouteArgs(string arguments)
    {
        // 由 NetshArgs.AddRoute 生成、形状受白名单锁死：prefix=a.b.c.d/n interface="X" nexthop=g.g.g.g ...
        string prefix = Between(arguments, "prefix=", ' ');
        int q1 = arguments.IndexOf("\"", StringComparison.Ordinal) + 1;
        string iface = arguments[q1..arguments.IndexOf('"', q1)];
        string nexthop = Between(arguments, "nexthop=", ' ');
        return (iface, prefix, nexthop);
    }

    private static string Between(string text, string open, char close)
    {
        int a = text.IndexOf(open, StringComparison.Ordinal) + open.Length;
        int b = text.IndexOf(close, a);
        return b < 0 ? text[a..] : text[a..b];
    }

    private IReadOnlyList<SplitLedgerRoute> LedgerRoutes(SplitRequest request)
    {
        var routes = new List<SplitLedgerRoute>
        {
            new("0.0.0.0/0", request.Wan.Gateway, request.Wan.Name, request.Wan.IfIndex, IsDefaultRoute: true),
        };
        routes.AddRange(request.Cidrs.Select(static c => SplitCidr.Normalize(c)!)
            .Select(cidr => new SplitLedgerRoute(cidr, request.Lan.Gateway, request.Lan.Name, request.Lan.IfIndex, false)));
        return routes;
    }

    private static void ReportVerify(SplitVerifyReport v, Action<string> onLine)
    {
        onLine($"[分流] 验证：默认路由 {(v.DefaultRouteUniqueOnWan ? "✅ 唯一走外网口" : "❌ " + v.DefaultRoutesSummary)}"
            + $" · 外网 {(v.WanReachable ? $"✅ {v.WanAvgMs}ms" : "❌ 不通")}"
            + $" · 内网 {(v.LanReachable ? $"✅ {v.LanAvgMs}ms" : "❌ 不通")}");
    }
}
