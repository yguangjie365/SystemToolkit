using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Core.Network.SplitRoute;

namespace SystemToolkit.Tests;

/// <summary>
/// NET-7 双网卡分流 Core 域测试：netsh route 形状白名单（含注入形态拒绝）、路由表解析
/// （locale 无关三变体）、CIDR 闸门、预览计划、应用/验证/自动回滚编排、恢复、守护清退、
/// 重启自检、台账三态。命令面全程 <see cref="ScriptedRunner"/> 假件，不触真路由表。
/// </summary>
public class SplitRouteTests
{
    private const string LanName = "WLAN 2";
    private const string WanName = "LAN";

    private static readonly SplitNicSelection Wan = new(WanName, 11, "192.168.1.1");
    private static readonly SplitNicSelection Lan = new(LanName, 9, "10.8.0.1");

    private static SplitRequest Req(params string[] cidrs) =>
        new(Wan, Lan, cidrs.Length == 0 ? ["10.0.0.0/8"] : cidrs);

    /// <summary>验证判据全过的路由表态（默认唯一走外口）。Apply 成功路径的用例都要先切到这个态。</summary>
    private static void MakeVerifyPass(ScriptedRunner runner) =>
        runner.ShowRouteText = static _ =>
            "*  11  75  0.0.0.0/0      192.168.1.1   No\r\n" +
            "*  11  75  10.0.0.0/8     10.8.0.1      Yes\r\n";

    // ── 假件：脚本化命令通道 ──

    private sealed class ScriptedRunner : ICommandRunner
    {
        public List<(string File, string Args)> Calls { get; } = new();

        /// <summary>命中即失败的参数前缀（模拟执行中断）。</summary>
        public string? FailOnContains { get; set; }

        public Func<string, string> ShowRouteText { get; set; } = static _ =>
            "添加 IPv4 路由表\r\n" +
            "*  11  75  0.0.0.0/0      192.168.1.1   No\r\n" +
            "*   9  75  0.0.0.0/0      10.8.0.1      No\r\n" +
            "*   9  75  10.8.0.0/24    10.8.0.1      Yes\r\n";

        public async Task<int> RunAsync(
            string fileName, string arguments, Action<string> onLine,
            CancellationToken ct = default, TimeSpan? timeout = null)
        {
            await Task.Yield();
            Calls.Add((fileName, arguments));
            if (FailOnContains is not null && arguments.Contains(FailOnContains, StringComparison.Ordinal))
            {
                return 1;
            }

            if (arguments.Contains("show route", StringComparison.Ordinal))
            {
                foreach (string line in ShowRouteText(arguments).Split("\r\n"))
                {
                    onLine(line);
                }
            }
            else if (arguments.Contains("show interfaces", StringComparison.Ordinal))
            {
                foreach (string line in new[]
                {
                    "Idx   Met  MTU  状态  名称",
                    "====================",
                    "  11    25    75  connected  LAN",
                    "   9    30    75  connected  WLAN 2",
                })
                {
                    onLine(line);
                }
            }

            return 0;
        }
    }

    /// <summary>逐主机量化控制的探针假件（外网/内网判据分开喂）。</summary>
    private sealed class SplitProbe : INetProbe
    {
        public Dictionary<string, PingQuantifyResult> Quantify { get; } = new(StringComparer.Ordinal);

        public void Ok(string host) => Quantify[host] = new PingQuantifyResult(2, 2, 4, 5, 6);

        public void Dead(string host) => Quantify[host] = new PingQuantifyResult(2, 0, null, null, null);

        public Task<bool> PingAsync(string address, int timeoutMs, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> ResolveAsync(string host, CancellationToken ct = default) => Task.FromResult(true);

        public Task<PingQuantifyResult> PingQuantifyAsync(
            string host, int count, int timeoutMs, int intervalMs, CancellationToken ct = default) =>
            Task.FromResult(Quantify.TryGetValue(host, out PingQuantifyResult? r)
                ? r
                : new PingQuantifyResult(count, 0, null, null, null));

        public Task<bool> PingDontFragmentAsync(string host, int payloadSize, int timeoutMs, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<TcpProbeResult> ConnectTcpAsync(string host, int port, int timeoutMs, CancellationToken ct = default) =>
            Task.FromResult(new TcpProbeResult(true, 1, null));
    }

    private sealed record Harness(
        SplitRouteService Svc,
        ScriptedRunner Runner,
        SplitProbe Probe,
        SplitLedgerStore Store,
        string LedgerPath,
        string Dir) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Dir))
                {
                    Directory.Delete(Dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响判定
            }
        }
    }

    private static Harness NewHarness()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"st-split-{Guid.NewGuid():N}");
        string ledgerPath = Path.Combine(dir, "ledger.json");
        var store = new SplitLedgerStore(ledgerPath);
        var runner = new ScriptedRunner();
        var probe = new SplitProbe();
        var svc = new SplitRouteService(runner, probe, store, new NetworkTestFakes.FakeSnapshotService());
        return new Harness(svc, runner, probe, store, ledgerPath, dir);
    }

    // ── 白名单形状 ──

    [Theory]
    [InlineData("interface ipv4 add route prefix=10.0.0.0/8 interface=\"WLAN 2\" nexthop=10.8.0.1 store=persistent metric=25", true)]
    [InlineData("interface ipv4 add route prefix=0.0.0.0/0 interface=\"LAN\" nexthop=192.168.1.1 store=active", true)]
    [InlineData("interface ipv4 delete route prefix=10.0.0.0/8 interface=\"WLAN 2\" nexthop=10.8.0.1", true)]
    [InlineData("interface ipv4 add route prefix=10.0.0.0/33 interface=\"WLAN 2\" nexthop=10.8.0.1 store=persistent metric=25", false)]
    [InlineData("interface ipv4 add route prefix=999.0.0.0/8 interface=\"WLAN 2\" nexthop=10.8.0.1 store=persistent metric=25", false)]
    [InlineData("interface ipv4 add route prefix=10.0.0.0/8 interface=\"WLAN 2\" nexthop=10.8.0.1 store=evil metric=25", false)]
    [InlineData("interface ipv4 add route prefix=10.0.0.0/8 interface=\"WLAN 2\" nexthop=10.8.0.1 store=persistent metric=0", false)]
    [InlineData("interface ipv4 add route prefix=10.0.0.0/8 interface=\"WLAN 2\" nexthop=10.8.0.1 store=persistent\" ; calc\" metric=25", false)]
    [InlineData("interface ipv4 delete route prefix=10.0.0.0/8 interface=\"WLAN 2\" nexthop=10.8.0.1/8", false)]
    public void RouteShapes_WhitelistGate(string args, bool allowed) =>
        Assert.Equal(allowed, NetshTokenRules.IsElevatedWrite("netsh", args));

    [Fact]
    public void NetshArgs_RouteBuilders_MatchWhitelistShapes()
    {
        Assert.True(NetshTokenRules.IsElevatedWrite("netsh",
            NetshArgs.AddRoute(LanName, "10.0.0.0/8", "10.8.0.1", persistent: true, 25)));
        Assert.True(NetshTokenRules.IsElevatedWrite("netsh",
            NetshArgs.DeleteRoute(LanName, "10.0.0.0/8", "10.8.0.1")));
        Assert.Contains("interface=\"WLAN 2\"",
            NetshArgs.AddRoute(LanName, "10.0.0.0/8", "10.8.0.1", false, 25), StringComparison.Ordinal);
    }

    // ── 路由表解析 ──

    [Fact]
    public void RouteParser_HandlesVariants_AndSkipsHeaders()
    {
        IReadOnlyList<RouteRow> rows = RouteTableParser.Parse([
            "添加 IPv4 路由表",
            "====================",
            "*  11  75  0.0.0.0/0      192.168.1.1   No",
            "  9    10.8.0.0/24  10.8.0.1",
            "*  11  75  0.0.0.0/0      10.8.0.1",
            "  4  75  224.0.0.0/4      0.0.0.0",
            "  4  75  10.0.0.0/99      1.2.3.4",
        ]);
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(static r => r.IsDefault));
        Assert.Equal(11, rows[0].IfIndex);
        Assert.Equal("10.8.0.0/24", rows[1].Prefix);
        Assert.Equal("10.8.0.1", rows[2].Nexthop);
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData(" 172.16.0.0/12 ", "172.16.0.0/12")]
    [InlineData("10.0.0.0/33", null)]
    [InlineData("abc/8", null)]
    [InlineData("10.0.0.0", null)]
    [InlineData("", null)]
    public void Cidr_Gate(string input, string? expected) =>
        Assert.Equal(expected, SplitCidr.Normalize(input));

    // ── 预览计划 ──

    [Fact]
    public async Task Preview_BuildsOrderedOps_FromLiveTableOnly()
    {
        using Harness h = NewHarness();
        IReadOnlyList<SplitOp> ops = await h.Svc.PreviewAsync(Req("10.0.0.0/8"));

        Assert.Equal(SplitOpKind.DeleteRoute, ops[0].Kind);
        Assert.Contains("interface=\"WLAN 2\"", ops[0].Arguments, StringComparison.Ordinal); // 内网卡默认先摘
        Assert.Contains("delete route", ops[1].Arguments, StringComparison.Ordinal);           // 外网卡原默认清理
        Assert.Contains("add route prefix=0.0.0.0/0", ops[2].Arguments, StringComparison.Ordinal);
        Assert.Contains("add route prefix=10.0.0.0/8", ops[3].Arguments, StringComparison.Ordinal);
        Assert.Equal(SplitOpKind.SetInterfaceMetric, ops[^1].Kind);
        Assert.Contains("metric=35", ops[^1].Arguments, StringComparison.Ordinal);
        Assert.All(ops, op => Assert.Equal("netsh", op.FileName)); // 预览计划全 netsh（renew 只在回滚出现）
    }

    [Fact]
    public async Task Preview_RejectsSameNicAndBadCidr()
    {
        using Harness h = NewHarness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Svc.PreviewAsync(
            new SplitRequest(Wan, new SplitNicSelection(WanName, 11, "192.168.1.1"), ["10.0.0.0/8"])));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Svc.PreviewAsync(Req("10.0.0.0/99")));
    }

    // ── 应用编排 ──

    [Fact]
    public async Task Apply_Success_WritesLedger_AndPassesVerify()
    {
        using Harness h = NewHarness();
        MakeVerifyPass(h.Runner);
        h.Probe.Ok(SplitRouteService.ExternalProbeHost);
        h.Probe.Ok(Lan.Gateway);

        SplitApplyOutcome outcome = await h.Svc.ApplyAsync(Req("10.0.0.0/8"), _ => { });

        Assert.True(outcome.Success);
        Assert.True(outcome.Verification!.AllPassed);
        Assert.Equal(SplitLedgerLoadStatus.Ok, h.Store.Load().State);
        SplitLedger ledger = h.Store.Load().Ledger!;
        Assert.Equal(2, ledger.Routes.Count); // 外默认 + 内段
        Assert.Equal(30, ledger.LanOriginalInterfaceMetric); // 来自 show interfaces 实测
        Assert.Contains(h.Runner.Calls, c => c.Args.Contains("add route prefix=0.0.0.0/0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_VerifyFails_AutoRollsBack_WithRenew()
    {
        using Harness h = NewHarness();
        h.Probe.Dead(SplitRouteService.ExternalProbeHost); // 外网不通 + 默认表仍双条
        h.Probe.Ok(Lan.Gateway);

        SplitApplyOutcome outcome = await h.Svc.ApplyAsync(Req("10.0.0.0/8"), _ => { });

        Assert.False(outcome.Success);
        Assert.True(outcome.RolledBack);
        Assert.False(File.Exists(h.LedgerPath)); // 失败不留台账
        Assert.Contains(h.Runner.Calls, c => c.File == "ipconfig" && c.Args.Contains("/renew", StringComparison.Ordinal));
        Assert.Contains(h.Runner.Calls, c =>
            c.Args.Contains("delete route prefix=0.0.0.0/0 interface=\"LAN\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_StepFailure_RollsBackBeforeVerify()
    {
        using Harness h = NewHarness();
        h.Runner.FailOnContains = "prefix=10.0.0.0/8"; // 内段添加这步失败

        SplitApplyOutcome outcome = await h.Svc.ApplyAsync(Req("10.0.0.0/8"), _ => { });

        Assert.True(outcome.RolledBack);
        Assert.Contains("10.0.0.0/8", outcome.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(h.LedgerPath));
    }

    [Fact]
    public async Task Apply_RejectsWhenLedgerAlreadyActive()
    {
        using Harness h = NewHarness();
        Directory.CreateDirectory(h.Dir);
        h.Store.Save(new SplitLedger(1, DateTimeOffset.Now, Wan, Lan, 30, []));

        SplitApplyOutcome outcome = await h.Svc.ApplyAsync(Req(), _ => { });
        Assert.False(outcome.Success);
        Assert.Contains("已有分流", outcome.Message, StringComparison.Ordinal);
    }

    // ── 恢复 / 守护 / 自检 ──

    private static async Task ApplyOkAsync(Harness h)
    {
        MakeVerifyPass(h.Runner);
        h.Probe.Ok(SplitRouteService.ExternalProbeHost);
        h.Probe.Ok(Lan.Gateway);
        SplitApplyOutcome outcome = await h.Svc.ApplyAsync(Req("10.0.0.0/8", "172.16.0.0/12"), _ => { });
        Assert.True(outcome.Success);
        h.Runner.Calls.Clear();
    }

    [Fact]
    public async Task Restore_DeletesLedgerRoutes_RestoresMetric_AndRenews()
    {
        using Harness h = NewHarness();
        await ApplyOkAsync(h);

        Assert.True(await h.Svc.RestoreAsync(_ => { }));
        Assert.Contains(h.Runner.Calls, c => c.Args.Contains("delete route prefix=0.0.0.0/0", StringComparison.Ordinal));
        Assert.Contains(h.Runner.Calls, c => c.Args.Contains("delete route prefix=172.16.0.0/12", StringComparison.Ordinal));
        Assert.Contains(h.Runner.Calls, c => c.Args.Contains("metric=30", StringComparison.Ordinal)); // 回原值
        Assert.Contains(h.Runner.Calls, c => c.File == "ipconfig");
        Assert.False(h.Svc.HasActiveLedger);
    }

    [Fact]
    public async Task GuardOnce_SweepsBackflowDefault_OnLanNic_Only()
    {
        using Harness h = NewHarness();
        await ApplyOkAsync(h);
        h.Runner.ShowRouteText = static _ =>
            "*  11  75  0.0.0.0/0      192.168.1.1   No\r\n" +
            "*   9  75  0.0.0.0/0      10.8.0.1      No\r\n"; // DHCP 回潮

        Assert.Equal(1, await h.Svc.GuardOnceAsync());
        Assert.Single(h.Runner.Calls, c =>
            c.Args.Contains("delete route prefix=0.0.0.0/0 interface=\"WLAN 2\" nexthop=10.8.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuardOnce_NoLedger_IsNoop()
    {
        using Harness h = NewHarness();
        Assert.Equal(0, await h.Svc.GuardOnceAsync());
        Assert.All(h.Runner.Calls, c => Assert.Contains("show", c.Args, StringComparison.Ordinal)); // 只有读
    }

    [Fact]
    public async Task SelfCheck_ReportsMissingLedgerRoutes()
    {
        using Harness h = NewHarness();
        await ApplyOkAsync(h);
        h.Runner.ShowRouteText = static _ =>
            "*  11  75  0.0.0.0/0      192.168.1.1   No\r\n" +
            "*   9  75  10.0.0.0/8     10.8.0.1      Yes\r\n"; // 172 段缺失

        IReadOnlyList<SplitLedgerRoute> missing = await h.Svc.SelfCheckAsync();
        Assert.Contains(missing, r => r.Prefix == "172.16.0.0/12");
        Assert.DoesNotContain(missing, static r => r.Prefix == "10.0.0.0/8");
    }

    // ── 台账存储 ──

    [Fact]
    public void LedgerStore_Missing_Save_Load_RoundTrip_AndClear()
    {
        string path = Path.Combine(Path.GetTempPath(), $"st-split-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SplitLedgerStore(path);
            Assert.Equal(SplitLedgerLoadStatus.Missing, store.Load().State);

            var ledger = new SplitLedger(1, DateTimeOffset.Now, Wan, Lan, 30,
                [new SplitLedgerRoute("0.0.0.0/0", "192.168.1.1", WanName, 11, true)]);
            store.Save(ledger);
            Assert.Equal(SplitLedgerLoadStatus.Ok, store.Load().State);
            Assert.Equal(ledger.Routes, store.Load().Ledger!.Routes);

            store.Clear();
            Assert.Equal(SplitLedgerLoadStatus.Missing, store.Load().State);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LedgerStore_Corrupted_DoesNotThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"st-split-ledger-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{broken");
            Assert.Equal(SplitLedgerLoadStatus.Corrupted, new SplitLedgerStore(path).Load().State);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
