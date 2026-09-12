using System.Runtime.Versioning;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Core.Network.SplitRoute;

/// <summary>
/// 读取 / 验证 / 守护半区（NET-7）：路由表与接口跃点读取（读命令直连不提权）、三判据验证、
/// DHCP 回潮守护（参考项目共识：续租把内网卡默认路由加回来 = 分流悄悄失效头号原因）、
/// 重启自检（持久路由台账在位性）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SplitRouteService
{
    /// <summary>读当前 IPv4 路由表（netsh 读命令，CommandRunner 直连不触发 UAC）。</summary>
    public async Task<IReadOnlyList<RouteRow>> ReadRouteTableAsync(CancellationToken ct = default)
    {
        List<string> lines = [];
        await _runner.RunAsync("netsh", "interface ipv4 show route", lines.Add, ct).ConfigureAwait(false);
        return RouteTableParser.Parse(lines);
    }

    /// <summary>三判据验证：默认路由唯一走外网口 / 外网连通（阿里 DNS）/ 内网网关可达。</summary>
    public async Task<SplitVerifyReport> VerifyAsync(SplitRequest request, CancellationToken ct = default)
    {
        IReadOnlyList<RouteRow> table = await ReadRouteTableAsync(ct).ConfigureAwait(false);
        var defaults = table.Where(static r => r.IsDefault).ToList();
        bool uniqueOnWan = defaults.Count == 1 && defaults[0].IfIndex == request.Wan.IfIndex
            && string.Equals(defaults[0].Nexthop, request.Wan.Gateway, StringComparison.Ordinal);

        PingQuantifyResult wan = await _probe.PingQuantifyAsync(
            ExternalProbeHost, count: 2, timeoutMs: 1500, intervalMs: 200, ct: ct).ConfigureAwait(false);
        PingQuantifyResult lan = await _probe.PingQuantifyAsync(
            request.Lan.Gateway, count: 2, timeoutMs: 1000, intervalMs: 200, ct: ct).ConfigureAwait(false);

        List<string> problems = [];
        if (!uniqueOnWan)
        {
            problems.Add($"默认路由不唯一或未走外网口（{defaults.Count} 条）");
        }

        if (wan.Received == 0)
        {
            problems.Add($"外网不通（{ExternalProbeHost} 无应答）");
        }

        if (lan.Received == 0)
        {
            problems.Add($"内网网关不可达（{request.Lan.Gateway}）");
        }

        return new SplitVerifyReport(
            uniqueOnWan,
            string.Join("、", defaults.Select(static r => $"if{r.IfIndex}→{r.Nexthop}")),
            wan.Received > 0, wan.AvgMs,
            lan.Received > 0, lan.AvgMs,
            problems);
    }

    /// <summary>
    /// 守护单轮：清退内网卡回潮的默认路由（DHCP 续租加回），返回清退条数。
    /// 台账不在位（未应用/已恢复）→ 0 轮空转，调用方可据此停表。
    /// </summary>
    public async Task<int> GuardOnceAsync(CancellationToken ct = default)
    {
        (SplitLedgerLoadStatus State, SplitLedger? Ledger) = _ledgerStore.Load();
        if (State != SplitLedgerLoadStatus.Ok || Ledger is null)
        {
            return 0;
        }

        IReadOnlyList<RouteRow> table = await ReadRouteTableAsync(ct).ConfigureAwait(false);
        List<RouteRow> backflow = [.. table.Where(r => r.IsDefault && r.IfIndex == Ledger.Lan.IfIndex)];
        if (backflow.Count == 0)
        {
            return 0; // 无回潮：不占动作名（周期空转零噪音——采样器不接判例同界）
        }

        using LogTiming timing = _log.Time(GuardAction);
        foreach (RouteRow row in backflow)
        {
            await _runner.RunAsync("netsh",
                NetshArgs.DeleteRoute(Ledger.Lan.Name, "0.0.0.0/0", row.Nexthop),
                _ => { }, ct).ConfigureAwait(false);
        }

        timing.Complete(LogResult.Success, LogLevel.Warn,
            $"{GuardAction} 清退内网卡回潮默认路由 {backflow.Count} 条（{Ledger.Lan.Name}）");
        return backflow.Count;
    }

    /// <summary>
    /// 守护循环：每轮 <paramref name="interval"/> 巡检一次（60s 量级），回潮即清退并回调计数；
    /// 取消即停（ContinuousPingService 同款收口纪律——切页/关窗必停，不留后台幽灵）。
    /// </summary>
    public async Task RunGuardLoopAsync(
        TimeSpan interval, Func<int, Task>? onSwept, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(true);
                int swept = await GuardOnceAsync(ct).ConfigureAwait(true);
                if (swept > 0 && onSwept is not null)
                {
                    await onSwept(swept).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停表
        }
    }

    /// <summary>
    /// 重启自检：台账逐条比对路由表，返回缺失项（UI 黄条「持久路由 X 条缺失，一键重应用」的数据源）。
    /// 未应用态返回空清单（无缺）。
    /// </summary>
    public async Task<IReadOnlyList<SplitLedgerRoute>> SelfCheckAsync(CancellationToken ct = default)
    {
        (SplitLedgerLoadStatus State, SplitLedger? Ledger) = _ledgerStore.Load();
        if (State != SplitLedgerLoadStatus.Ok || Ledger is null)
        {
            return [];
        }

        IReadOnlyList<RouteRow> table = await ReadRouteTableAsync(ct).ConfigureAwait(false);
        return Ledger.Routes
            .Where(route => !table.Any(row =>
                row.IfIndex == route.IfIndex
                && string.Equals(row.Prefix, route.Prefix, StringComparison.Ordinal)
                && string.Equals(row.Nexthop, route.Nexthop, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>接口清单（索引/名称/当前跃点）——UI 组网卡候选卡的数据源。</summary>
    public async Task<IReadOnlyList<RouteTableParser.InterfaceRow>> ReadInterfacesAsync(CancellationToken ct = default)
    {
        List<string> lines = [];
        await _runner.RunAsync("netsh", "interface ipv4 show interfaces", lines.Add, ct).ConfigureAwait(false);
        return RouteTableParser.ParseInterfaces(lines);
    }

    /// <summary>窥视当前台账（null=未应用态）；UI 回显选卡与台账表。</summary>
    public SplitLedger? PeekLedger() => _ledgerStore.Load().Ledger;

    private async Task<int> ReadLanInterfaceMetricAsync(string lanName, CancellationToken ct)
    {
        List<string> lines = [];
        await _runner.RunAsync("netsh", "interface ipv4 show interfaces", lines.Add, ct).ConfigureAwait(false);
        InterfaceMetricInfo? row = InterfaceTableParser.Parse(lines)
            .FirstOrDefault(i => string.Equals(i.Name, lanName, StringComparison.OrdinalIgnoreCase));
        return row?.Metric ?? 0; // 0 = 读不到（自动跃点态），台账据此跳过跃点恢复
    }
}
