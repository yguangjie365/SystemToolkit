using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Modules.NetManager;

namespace SystemToolkit.Tests;

/// <summary>
/// 🔴 v20-NM-🟠-1 回归锁（2026-09-16）：<b>DiagStep 标识的跨层契约</b>。
/// <para>
/// <b>为什么需要这条锁</b>：修复前，Core 的 <see cref="DiagStepResult.Step"/> 直接携带**中文展示文案**，
/// Core 与 UI 各自硬编码字面量、靠注释约定「逐字一致」。任何一侧改字都会让 UI 的
/// <c>Steps.First(s =&gt; s.Step == r.Step)</c> 抛 <c>InvalidOperationException</c>，
/// 经 <c>Progress&lt;T&gt;.Report</c> 冒泡到 VM 的 catch ⇒ 用户看到面向开发者的
/// “Sequence contains no matching element”，而不是“诊断失败”。<b>全程无编译错误、无守卫告警。</b>
/// </para>
/// <para>
/// 修复后标识是 <see cref="DiagStep"/> 常量（编译期防错），但**Core 的产出点**与
/// **UI 的登记表**仍是两份独立清单 —— 只靠常量类型无法保证「UI 登记了 Core 会产出的全部标识」。
/// 本类把这个不变量钉死：
/// <list type="number">
///   <item>Core 实际产出的每个 <c>Step</c> 都必须落在 UI 的 <c>StepDefs</c> 表内（否则对位必抛）；</item>
///   <item>UI 登记的每个标识都必须被 Core 产出（否则界面上有一行永远停在「待检测」）；</item>
///   <item>两侧的总数一致（清单驱动的守卫必须断言总数，防"只补一半"被判通过）。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 断言的取数方式是**跑真实诊断链**（fake 探针），不是读常量表自比 —— 后者只能证明
/// 常量表与它自己一致，证明不了产出点用的是这些常量。
/// </para>
/// </summary>
public class DiagStepContractTests
{
    /// <summary>
    /// 跑一轮真实诊断链（全连通 → 五步都会走到，丢包量化也能量化），
    /// 返回 Core 实际产出的步骤标识（终态 / Running 两批）。
    /// <para>每次调用都用**全新** fake 实例，避免用例间共享可变状态（xUnit 同 class 内用例可并行）。</para>
    /// </summary>
    private static async Task<(List<string> Final, List<string> Running)> RunChainAsync()
    {
        var info = new NetworkTestFakes.FakeInfoService();
        var probe = new NetworkTestFakes.FakeProbe();
        info.Adapters.Add(new NetAdapterInfo(
            Name: "以太网", Description: "fake", Type: NetType.Ethernet, Status: OperStatus.Up,
            SpeedMbps: 1000, MacAddress: "AA:BB:CC:DD:EE:FF", IsDhcp: true,
            IPv4WithMask: new[] { "192.168.1.10/24" }, Gateways: new[] { "192.168.1.1" },
            DnsServers: new[] { "192.168.1.1" }));

        var reported = new List<DiagStepResult>();
        var service = new NetDiagnosticService(info, probe, new NetworkTestFakes.FakeHostsService());
        await service.RunWithProgressAsync(new InlineSink(reported.Add));

        return (
            reported.Where(s => s.Status != DiagStatus.Running).Select(s => s.Step).ToList(),
            reported.Where(s => s.Status == DiagStatus.Running).Select(s => s.Step).ToList());
    }

    /// <summary>同步进度收集器（不经 SyncContext 投递，规避 Progress&lt;T&gt; 的异步竞态）。</summary>
    private sealed class InlineSink : IProgress<DiagStepResult>
    {
        private readonly Action<DiagStepResult> _sink;

        public InlineSink(Action<DiagStepResult> sink) => _sink = sink;

        public void Report(DiagStepResult value) => _sink(value);
    }

    [Fact]
    public async Task CoreProducedStepIds_AllRegisteredInUiStepDefs()
    {
        (List<string> final, List<string> running) = await RunChainAsync();

        // 断言取数自**实际产出**，不是读常量表自比
        Assert.NotEmpty(final);
        Assert.Equal(final, running); // Running 与终态同标识（UI 按标识对位，两态必须同名）

        string[] registered = NetDiagnosticsTabViewModel.StepDefs.Select(d => d.Id).ToArray();

        var missing = final.Where(id => !registered.Contains(id, StringComparer.Ordinal)).ToList();
        Assert.True(
            missing.Count == 0,
            $"Core 产出了 UI 未登记的步骤标识：{string.Join(", ", missing)}——" +
            $"UI 的 Steps.First(s => s.StepId == r.Step) 会抛 InvalidOperationException。" +
            $"Core 产出 = [{string.Join(", ", final)}]；UI 登记 = [{string.Join(", ", registered)}]");

        var extra = registered.Where(id => !final.Contains(id, StringComparer.Ordinal)).ToList();
        Assert.True(
            extra.Count == 0,
            $"UI 登记了 Core 从不产出的标识：{string.Join(", ", extra)}——该行会永远停在「待检测」。");
    }

    /// <summary>
    /// 🔴 清单驱动守卫的配套断言：两侧总数相等。
    /// 防"只补一半"——若 Core 新增一步而 UI 只登记了其中一部分，上面的逐项断言会红；
    /// 但若两侧各自都少一条（例如 UI 抄漏一条、Core 也恰好在测试路径上没产出它），逐项断言会一起绿。
    /// </summary>
    [Fact]
    public async Task StepCount_UiMatchesCore()
    {
        (List<string> final, _) = await RunChainAsync();

        Assert.Equal(NetDiagnosticService.StepIds.Length, final.Count);
        Assert.Equal(NetDiagnosticService.StepIds.Length, NetDiagnosticsTabViewModel.StepDefs.Length);
    }

    /// <summary>标识必须唯一且非空（Core 侧静态构造已在类型初始化时抛，这里再钉一条可读断言的回归）。</summary>
    [Fact]
    public void StepIds_AreUniqueAndNonEmpty()
    {
        string[] ids = NetDiagnosticService.StepIds;

        Assert.NotEmpty(ids);
        Assert.DoesNotContain(ids, string.IsNullOrEmpty);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>UI 的展示文案必须非空（标识与文案解耦后，文案漏填不会影响对位、因此更需要独立断言）。</summary>
    [Fact]
    public void UiStepLabels_AreNonEmptyAndUnique()
    {
        (string Id, string Label)[] defs = NetDiagnosticsTabViewModel.StepDefs;

        Assert.DoesNotContain(defs, d => string.IsNullOrWhiteSpace(d.Label));
        Assert.Equal(defs.Length, defs.Select(d => d.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(defs.Length, defs.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
