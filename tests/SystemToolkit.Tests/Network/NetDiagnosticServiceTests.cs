using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 诊断服务测试：经 fake INetProbe 覆盖结论规则的全部分支——真实网络不可控，
/// 结论规则必须靠可编程的连通性组合来钉死（含「ICMP 被过滤」分支，防误报断网）。
/// 自旧工程移植，L15 英文命名；新增 RunWithProgressAsync 契约用例。
/// </summary>
public class NetDiagnosticServiceTests
{
    /// <summary>同步进度收集器：不经 SyncContext 投递，规避 Progress&lt;T&gt; 的异步竞态。</summary>
    private sealed class InlineProgress : IProgress<DiagStepResult>
    {
        private readonly Action<DiagStepResult> _sink;

        public InlineProgress(Action<DiagStepResult> sink) => _sink = sink;

        public void Report(DiagStepResult value) => _sink(value);
    }

    private readonly NetworkTestFakes.FakeInfoService _info = new();
    private readonly NetworkTestFakes.FakeProbe _probe = new();

    private NetDiagnosticService Create() => new(_info, _probe, new NetworkTestFakes.FakeHostsService());

    private static NetAdapterInfo UpAdapter(string gateway = "192.168.1.1") => new(
        Name: "以太网", Description: "fake", Type: NetType.Ethernet, Status: OperStatus.Up,
        SpeedMbps: 1000, MacAddress: "AA:BB:CC:DD:EE:FF", IsDhcp: true,
        IPv4WithMask: new[] { "192.168.1.10/24" }, Gateways: gateway.Length == 0 ? Array.Empty<string>() : new[] { gateway },
        DnsServers: new[] { "192.168.1.1" });

    private static DiagStatus StatusOf(IReadOnlyList<DiagStepResult> steps, string step)
        => steps.First(s => s.Step == step).Status;

    [Fact]
    public async Task RunAsync_NoConnectedAdapter_FiveStepsNeverShortCircuit()
    {
        _info.Adapters.Add(new NetAdapterInfo("以太网", "fake", NetType.Ethernet, OperStatus.Down, 1000, "AA", true,
            new[] { "192.168.1.10/24" }, new[] { "192.168.1.1" }, Array.Empty<string>()));

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        // 不短路：即使首步失败，公网与 DNS 仍会探测，一次跑完能看清整条链路（含丢包量化共 5 步）
        Assert.Equal(5, steps.Count);
        Assert.Equal(DiagStatus.Failed, StatusOf(steps, "适配器"));
        Assert.Equal(DiagStatus.Skipped, StatusOf(steps, "网关"));
        Assert.Contains("未检测到", Create().BuildConclusion(steps));
    }

    [Fact]
    public async Task GatewayUnreachable_ConcludedLocalLinkFailure()
    {
        _info.Adapters.Add(UpAdapter());
        _probe.GatewayReachable = false;

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        Assert.Equal(DiagStatus.Failed, StatusOf(steps, "网关"));
        Assert.Contains("本地链路故障", Create().BuildConclusion(steps));
    }

    [Fact]
    public async Task PublicAndDnsDown_ConcludedOfflineOrIsp()
    {
        _info.Adapters.Add(UpAdapter());
        _probe.PublicReachable = false;
        _probe.DnsResolvable = false;

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        Assert.Contains("断网或运营商故障", Create().BuildConclusion(steps));
    }

    [Fact]
    public async Task PublicFilteredButDnsOk_ConcludedIcmpFiltered()
    {
        // 【v0.2 新增分支】最容易被误判为断网的场景：许多网络禁 Ping 但上网正常
        _info.Adapters.Add(UpAdapter());
        _probe.PublicReachable = false;
        _probe.DnsResolvable = true;

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();
        string conclusion = Create().BuildConclusion(steps);

        // 关键：结论里不能出现「断网 / 故障」的定性（文案里的「不代表断网」是安抚措辞，不冲突）
        Assert.Contains("ICMP 被过滤", conclusion);
        Assert.DoesNotContain("疑似断网", conclusion);
        Assert.DoesNotContain("本地链路故障", conclusion);
        Assert.DoesNotContain("运营商故障", conclusion);
    }

    [Fact]
    public async Task PublicOkDnsFailed_ConcludedDnsIssue()
    {
        _info.Adapters.Add(UpAdapter());
        _probe.DnsResolvable = false;

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        Assert.Contains("DNS 异常", Create().BuildConclusion(steps));
    }

    [Fact]
    public async Task AllStepsPass_ConcludedHealthy()
    {
        _info.Adapters.Add(UpAdapter());

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        Assert.Equal(DiagStatus.Success, StatusOf(steps, "适配器"));
        Assert.Equal(DiagStatus.Success, StatusOf(steps, "网关"));
        Assert.Equal(DiagStatus.Success, StatusOf(steps, "公网"));
        Assert.Equal(DiagStatus.Success, StatusOf(steps, "DNS 解析"));
        Assert.Contains("网络正常", Create().BuildConclusion(steps));
    }

    [Fact]
    public async Task Quantify_ZeroLoss_SuccessWithLatencyDistribution()
    {
        _info.Adapters.Add(UpAdapter());

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        DiagStepResult step = steps.First(s => s.Step == "丢包量化");
        Assert.Equal(DiagStatus.Success, step.Status);
        Assert.Contains("网关：丢包 0%，延迟 5/5/5 ms", step.Detail);
        Assert.Contains("公网：丢包 0%，延迟 5/5/5 ms", step.Detail);
    }

    [Fact]
    public async Task Quantify_WithLoss_FailedAndReportsLossRate()
    {
        _info.Adapters.Add(UpAdapter());
        _probe.QuantifyLost = 3;

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        DiagStepResult step = steps.First(s => s.Step == "丢包量化");
        Assert.Equal(DiagStatus.Failed, step.Status);
        Assert.Contains("丢包 30%", step.Detail);
    }

    [Fact]
    public async Task Quantify_IcmpFiltered_PublicReportsNotQuantifiable()
    {
        // 【M6a 降级】公网 Ping 全失败 + DNS 正常 = ICMP 被过滤 → 量化区显示无法量化而非 0%
        _info.Adapters.Add(UpAdapter());
        _probe.PublicReachable = false;
        _probe.DnsResolvable = true;

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        DiagStepResult step = steps.First(s => s.Step == "丢包量化");
        Assert.Contains("公网：无法量化", step.Detail);
        Assert.DoesNotContain("公网：丢包", step.Detail);
    }

    [Theory]
    [InlineData(new[] { 576, 577, 1472 }, 1472)]          // 全区间通过
    [InlineData(new[] { 576 }, 576)]                       // 仅最小尺寸通过
    public async Task BinarySearchMtu_BoundaryConverges(int[] passSizes, int expected)
    {
        // 【核实报告 N1】谓词改 async 后边界语义不变
        int? result = await NetDiagnosticService.BinarySearchMtuPayloadAsync(
            size => System.Threading.Tasks.Task.FromResult(passSizes.Contains(size)), 576, 1472);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task BinarySearchMtu_NothingPasses_ReturnsNull()
    {
        Assert.Null(await NetDiagnosticService.BinarySearchMtuPayloadAsync(
            _ => System.Threading.Tasks.Task.FromResult(false), 576, 1472));
    }

    [Fact]
    public async Task BinarySearchMtu_MidRange_ConvergesToBoundary()
    {
        // 路径 MTU 上限 = 载荷 1200 可通过、1201 不可通过 → 二分应收敛到 1200
        int? result = await NetDiagnosticService.BinarySearchMtuPayloadAsync(
            size => System.Threading.Tasks.Task.FromResult(size <= 1200), 576, 1472);

        Assert.Equal(1200, result);
    }

    [Fact]
    public async Task TestPort_SuccessAndRefused_PassedThrough()
    {
        _probe.TcpResult = new TcpProbeResult(true, 12, null);
        TcpProbeResult ok = await Create().TestPortAsync("example.org", 443);
        Assert.True(ok.Success);
        Assert.Equal(12, ok.LatencyMs);

        _probe.TcpResult = new TcpProbeResult(false, null, "连接被拒绝（端口未开放或服务未运行）");
        TcpProbeResult refused = await Create().TestPortAsync("example.org", 443);
        Assert.False(refused.Success);
        Assert.Contains("连接被拒绝", refused.Error);
    }

    [Fact]
    public async Task ProbeMtu_NothingPasses_ReturnsNull()
    {
        _probe.MaxDfPayloadPass = null;
        Assert.Null(await Create().ProbePathMtuAsync());
    }

    [Fact]
    public async Task ProbeMtu_StandardPath_Suggests1500()
    {
        _probe.MaxDfPayloadPass = 1472;
        MtuProbeResult? result = await Create().ProbePathMtuAsync();
        Assert.Equal(1500, result!.PathMtu);
        Assert.Equal(1500, result.SuggestedNicMtu);
    }

    [Fact]
    public async Task ProxyEnabled_ConclusionAppendsProxyHint()
    {
        _info.Adapters.Add(UpAdapter());
        _info.Proxy = new ProxyInfo(true, "127.0.0.1:7890");

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        Assert.Contains("系统代理", Create().BuildConclusion(steps));
    }

    [Fact]
    public async Task PublicProbe_UsesFixedIpNotDomain()
    {
        _info.Adapters.Add(UpAdapter());

        await Create().RunAsync();

        Assert.Contains(NetDiagnosticService.PublicProbeAddress, _probe.PingTargets);
    }

    [Fact]
    public async Task MultiAdapter_AnyGatewayReachable_Passes()
    {
        _info.Adapters.Add(UpAdapter("10.0.0.1"));
        _info.Adapters.Add(new NetAdapterInfo("无线 网络", "fake", NetType.Wireless, OperStatus.Up, 300, "BB", true,
            new[] { "10.0.0.5/24" }, new[] { "10.0.0.254" }, Array.Empty<string>()));
        _probe.PublicReachable = true;
        _probe.GatewayReachable = true;

        IReadOnlyList<DiagStepResult> steps = await Create().RunAsync();

        Assert.Equal(DiagStatus.Success, StatusOf(steps, "网关"));
        // 两个网关都被探测过（任一可达即通过，但仍全部记录）
        Assert.Contains("10.0.0.1", _probe.PingTargets);
    }

    [Fact]
    public async Task RunWithProgress_ReportsRunningThenFinalForEachStep()
    {
        // 【2026-09-06 新增契约】每步开始上报 Running 快照、结束上报终态（VM 按 Step 名对位刷新）
        // ——替代旧 UI 的假 Running 动画。5 步 = 10 次上报。
        _info.Adapters.Add(UpAdapter());

        // 同步收集器：Progress<T> 经 SyncContext 异步投递，断言会跑在最后一次回调之前（实测 9/10）
        var reported = new List<DiagStepResult>();
        await Create().RunWithProgressAsync(new InlineProgress(reported.Add));

        Assert.Equal(10, reported.Count);
        var runningSteps = reported.Where(s => s.Status == DiagStatus.Running).Select(s => s.Step).ToList();
        var finalSteps = reported.Where(s => s.Status != DiagStatus.Running).Select(s => s.Step).ToList();
        Assert.Equal(5, runningSteps.Count);
        Assert.Equal(finalSteps, runningSteps); // Running 与终态的步骤名一一对应
    }
}
