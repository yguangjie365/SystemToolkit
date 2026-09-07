using System.Diagnostics;
using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="INetDiagnosticService"/> 实现。
/// <para>
/// 步骤命名与顺序：适配器 → 网关 → 公网 → DNS 解析（结论不占步骤位，存 <see cref="Conclusion"/>）。
/// 多网卡时<b>每个网关都被探测</b>（任一可达即通过，但全部记录，便于看清多宿主环境）；
/// 公网探测用固定 IP（<see cref="PublicProbeAddress"/>）而非域名，避免与 DNS 探测互相干扰。
/// </para>
/// </summary>
public sealed class NetDiagnosticService : INetDiagnosticService
{
    /// <summary>Ping 超时（设计文档 §4.3：1500ms）。</summary>
    public const int PingTimeoutMs = 1500;

    /// <summary>公网探测固定地址（阿里公共 DNS，用 IP 避免依赖 DNS——DNS 坏了它也得能测）。</summary>
    public const string PublicProbeAddress = "223.5.5.5";

    private const string DnsProbeHost = "www.baidu.com";

    // 【M6a】量化探测参数（评审 Q1 结论：10 包 / 间隔 200ms / 单包超时 1s）
    /// <summary>量化探测发包数（把「断网」与「丢包」区分开，评审 Q1 结论）。</summary>
    public const int QuantifyCount = 10;
    /// <summary>量化探测单包超时（毫秒）。</summary>
    public const int QuantifyTimeoutMs = 1000;
    /// <summary>量化探测包间隔（毫秒；最后一轮后不再等待）。</summary>
    public const int QuantifyIntervalMs = 200;
    // MTU 二分区间：载荷 576..1472（1472 = 1500 − 28 IP+ICMP 头）
    /// <summary>MTU 二分探测的最小载荷（字节）；低于此仍不通视为路径完全不可用。</summary>
    public const int MtuMinPayload = 576;
    /// <summary>MTU 二分探测的最大载荷（字节；1472 = 1500 − 28 IP+ICMP 头）。</summary>
    public const int MtuMaxPayload = 1472;
    private const int MtuOverhead = 28;
    private const int MtuProbesTimeoutMs = 1000;
    // 端口测试（评审 Q5：3 秒超时）
    /// <summary>TCP 端口连通测试超时（毫秒，评审 Q5：3 秒）。</summary>
    public const int PortTestTimeoutMs = 3000;

    private readonly INetworkInfoService _info;
    private readonly INetProbe _probe;
    private readonly IHostsCheckService _hosts;

    /// <summary>依赖注入构造：信息采集 / 探针 / hosts 检查均经接口注入（测试以 fake 覆盖结论规则）。</summary>
    public NetDiagnosticService(INetworkInfoService info, INetProbe probe, IHostsCheckService hosts)
    {
        _info = info;
        _probe = probe;
        _hosts = hosts;
    }

    /// <summary>最近一次诊断的结论；未诊断过为空串。</summary>
    public string Conclusion { get; private set; } = "";

    /// <inheritdoc cref="INetDiagnosticService.RunAsync"/>
    public Task<IReadOnlyList<DiagStepResult>> RunAsync(CancellationToken ct = default)
        => RunWithProgressAsync(progress: null, ct);

    /// <inheritdoc cref="INetDiagnosticService.RunWithProgressAsync"/>
    public async Task<IReadOnlyList<DiagStepResult>> RunWithProgressAsync(IProgress<DiagStepResult>? progress, CancellationToken ct = default)
    {
        var results = new List<DiagStepResult>(4);

        // 结果进列表的同时上报进度（Running 快照在每步开始时上报，VM 按 Step 名对位刷新）
        void Add(DiagStepResult r)
        {
            results.Add(r);
            progress?.Report(r);
        }

        // ① 适配器
        ReportRunning(progress, "适配器");
        var clock = Stopwatch.StartNew();
        IReadOnlyList<NetAdapterInfo> adapters = await _info.GetAdaptersAsync().ConfigureAwait(false);
        var upAdapters = adapters.Where(a => a.Status == OperStatus.Up).ToList();
        clock.Stop();
        Add(new DiagStepResult(
            "适配器",
            upAdapters.Count > 0 ? DiagStatus.Success : DiagStatus.Failed,
            upAdapters.Count > 0
                ? $"检测到 {upAdapters.Count} 个已连接：{string.Join("、", upAdapters.Select(a => a.Name))}"
                : "未检测到已连接的适配器",
            clock.ElapsedMilliseconds));

        // ② 网关：Up 适配器的每个网关都探测（任一可达即通过，但全部记录——多宿主环境要看全貌）
        ReportRunning(progress, "网关");
        clock.Restart();
        string[] gateways = upAdapters.SelectMany(a => a.Gateways).Distinct().ToArray();
        var reachable = new List<string>();
        bool gatewayOk = false;
        if (gateways.Length == 0)
        {
            Add(new DiagStepResult("网关", DiagStatus.Skipped, "无网关可测（未检测到已连接适配器或未配置默认网关）", clock.ElapsedMilliseconds));
        }
        else
        {
            foreach (string gateway in gateways)
            {
                if (await _probe.PingAsync(gateway, PingTimeoutMs, ct).ConfigureAwait(false))
                {
                    reachable.Add(gateway);
                }
            }

            gatewayOk = reachable.Count > 0;
            // 【核实报告 N9】多宿主环境逐网关单包延迟（复用 PingQuantifyAsync count=1，零新接口）
            var gatewayDetails = new List<string>(reachable.Count);
            foreach (string gateway in reachable)
            {
                PingQuantifyResult latency = await _probe.PingQuantifyAsync(gateway, 1, PingTimeoutMs, 0, ct).ConfigureAwait(false);
                gatewayDetails.Add($"{gateway}（延迟 {latency.AvgMs} ms）");
            }

            Add(new DiagStepResult(
                "网关",
                gatewayOk ? DiagStatus.Success : DiagStatus.Failed,
                gatewayOk
                    ? $"网关可达：{string.Join("、", gatewayDetails)}"
                    : $"全部 {gateways.Length} 个网关不可达：{string.Join("、", gateways)}",
                clock.ElapsedMilliseconds));
        }

        // ③④ 公网 Ping 与 DNS 解析相互独立——【核实报告 N4】并行执行（断网时最坏耗时 ↓ 约 40%）；
        // 各自完成后立即上报（真实增量：断网时公网行先出结果，DNS 行不必陪跑假动画）
        ReportRunning(progress, "公网");
        ReportRunning(progress, "DNS 解析");
        clock.Restart();
        Task<bool> publicTask = _probe.PingAsync(PublicProbeAddress, PingTimeoutMs, ct);
        Task<bool> dnsTask = _probe.ResolveAsync(DnsProbeHost, ct);
        bool publicOk = await publicTask.ConfigureAwait(false);
        Add(new DiagStepResult(
            "公网",
            publicOk ? DiagStatus.Success : DiagStatus.Failed,
            publicOk ? $"{PublicProbeAddress} 可达" : $"{PublicProbeAddress} 不可达（对方禁 ICMP 也会如此）",
            clock.ElapsedMilliseconds));
        bool dnsOk = await dnsTask.ConfigureAwait(false);
        clock.Stop();
        Add(new DiagStepResult(
            "DNS 解析",
            dnsOk ? DiagStatus.Success : DiagStatus.Failed,
            dnsOk ? $"{DnsProbeHost} 解析成功" : $"{DnsProbeHost} 解析失败",
            clock.ElapsedMilliseconds));

        // ⑤ 丢包量化（M6a P0-1）：把「断网」与「网烂」区分开。
        // 目标裁剪（评审 Q2）：仅对可达目标量化——不可达侧结论已足够，避免 10 包全超时的无谓等待；
        // ICMP 过滤场景（公网 Ping 不通但 DNS 正常）显式「无法量化」而非 0% 丢包。
        ReportRunning(progress, "丢包量化");
        clock.Restart();
        Add(await QuantifyStepAsync(gatewayOk ? gateways.First(g => reachable.Contains(g)) : null, publicOk, clock, ct).ConfigureAwait(false));

        Conclusion = BuildConclusion(results);
        return results;
    }

    /// <summary>步骤开始快照：Running 状态、零耗时、空明细（VM 显示进行中样式）。</summary>
    private static void ReportRunning(IProgress<DiagStepResult>? progress, string step)
        => progress?.Report(new DiagStepResult(step, DiagStatus.Running, "检测中…", 0));

    private async Task<DiagStepResult> QuantifyStepAsync(string? gatewayTarget, bool publicOk, Stopwatch clock, CancellationToken ct)
    {
        var parts = new List<string>(2);
        var statuses = new List<DiagStatus>(2);

        if (gatewayTarget is not null)
        {
            PingQuantifyResult r = await _probe.PingQuantifyAsync(gatewayTarget, QuantifyCount, QuantifyTimeoutMs, QuantifyIntervalMs, ct).ConfigureAwait(false);
            parts.Add(DescribeQuantify("网关", r));
            statuses.Add(ToStepStatus(r));
        }
        else
        {
            parts.Add("网关：无可达网关，跳过");
        }

        if (publicOk)
        {
            PingQuantifyResult r = await _probe.PingQuantifyAsync(PublicProbeAddress, QuantifyCount, QuantifyTimeoutMs, QuantifyIntervalMs, ct).ConfigureAwait(false);
            parts.Add(DescribeQuantify("公网", r));
            statuses.Add(ToStepStatus(r));
        }
        else
        {
            // 公网 Ping 不可达（断网或 ICMP 过滤）：量化无意义，显示「无法量化」而非 0
            parts.Add("公网：无法量化（目标不可达）");
        }

        DiagStatus status = statuses.Count > 0 && statuses.All(s => s == DiagStatus.Success)
            ? DiagStatus.Success
            : statuses.Contains(DiagStatus.Failed) ? DiagStatus.Failed : DiagStatus.Skipped;
        return new DiagStepResult("丢包量化", status, string.Join("；", parts), clock.ElapsedMilliseconds);
    }

    private static string DescribeQuantify(string label, PingQuantifyResult r)
        => r.Received == 0
            ? $"{label}：丢包 100%"
            : $"{label}：丢包 {r.LossPercent}%，延迟 {r.MinMs}/{r.AvgMs}/{r.MaxMs} ms";

    private static DiagStatus ToStepStatus(PingQuantifyResult r)
        => r.LossPercent == 0 ? DiagStatus.Success : DiagStatus.Failed;

    /// <summary>
    /// 【M6a】MTU 路径二分（纯逻辑，谓词注入可测）：返回最大可通过载荷；全失败 → null。
    /// </summary>
    internal static async Task<int?> BinarySearchMtuPayloadAsync(Func<int, Task<bool>> fits, int minPayload, int maxPayload)
    {
        // 【核实报告 N1】谓词改 async（原版调用方 GetResult 同步阻塞 = sync-over-async 反模式）
        if (!await fits(minPayload).ConfigureAwait(false))
        {
            return null; // 最小尺寸都不可通过：无法收敛（全过滤 / 断网）
        }

        if (await fits(maxPayload).ConfigureAwait(false))
        {
            return maxPayload; // 全区间通过：路径 MTU ≥ 标准值
        }

        int lo = minPayload;
        int hi = maxPayload;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (await fits(mid).ConfigureAwait(false))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <inheritdoc cref="INetDiagnosticService.TestPortAsync"/>
    public Task<Models.TcpProbeResult> TestPortAsync(string host, int port, CancellationToken ct = default)
        => _probe.ConnectTcpAsync(host, port, PortTestTimeoutMs, ct);

    /// <inheritdoc cref="INetDiagnosticService.CheckHostsAsync"/>
    public Task<HostsCheckResult> CheckHostsAsync(CancellationToken ct = default)
        => _hosts.CheckAsync(ct);

    /// <inheritdoc cref="INetDiagnosticService.ProbePathMtuAsync"/>
    public async Task<Models.MtuProbeResult?> ProbePathMtuAsync(CancellationToken ct = default)
    {
        int? payload = await BinarySearchMtuPayloadAsync(
            size => _probe.PingDontFragmentAsync(PublicProbeAddress, size, MtuProbesTimeoutMs, ct),
            MtuMinPayload,
            MtuMaxPayload).ConfigureAwait(false);
        return payload is int p ? new Models.MtuProbeResult(p + MtuOverhead, p + MtuOverhead) : null;
    }

    /// <summary>
    /// 结论规则（设计文档 §4.3 对照表）。分支顺序即优先级：链路 → 断网 → ICMP 过滤 → DNS → 正常；
    /// 代理开启时在任意结论后追加提示（代理是「能连上网但部分应用不通」的常见隐因）。
    /// </summary>
    public string BuildConclusion(IReadOnlyList<DiagStepResult> steps)
    {
        bool hasUpAdapter = StatusOf(steps, "适配器") == DiagStatus.Success;
        bool gatewayTested = StatusOf(steps, "网关") != DiagStatus.Skipped;
        bool gatewayOk = StatusOf(steps, "网关") == DiagStatus.Success;
        bool publicOk = StatusOf(steps, "公网") == DiagStatus.Success;
        bool dnsOk = StatusOf(steps, "DNS 解析") == DiagStatus.Success;
        bool proxyEnabled = _info.GetSystemProxy().Enabled;

        string conclusion;
        if (!hasUpAdapter)
        {
            conclusion = "❌ 未检测到已连接的适配器：请检查网线 / WiFi 开关";
        }
        else if (gatewayTested && !gatewayOk)
        {
            conclusion = "❌ 本地链路故障：网关不可达，请检查网线 / WiFi，或到「网络修复」重启网卡";
        }
        else if (!publicOk && !dnsOk)
        {
            conclusion = "❌ 疑似断网或运营商故障：本地链路正常但公网与 DNS 均不通";
        }
        else if (!publicOk && dnsOk)
        {
            // 设计文档 §4.3 v0.2 新增分支：防把禁 Ping 的公网环境误报为断网
            conclusion = "⚠️ 公网 Ping 不通但 DNS 解析正常——多为 ICMP 被过滤，链路可能正常，不代表断网";
        }
        else if (publicOk && !dnsOk)
        {
            conclusion = "❌ DNS 异常：公网可达但域名解析失败，建议到「网络设置」切换公共 DNS";
        }
        else
        {
            conclusion = "✅ 网络正常";
        }

        return proxyEnabled
            ? conclusion + "\n⚠️ 系统代理当前开启——若部分应用无法上网，先尝试关闭代理（网络设置页）"
            : conclusion;
    }

    private static DiagStatus StatusOf(IReadOnlyList<DiagStepResult> steps, string step)
        => steps.FirstOrDefault(s => s.Step == step)?.Status ?? DiagStatus.Skipped;
}
