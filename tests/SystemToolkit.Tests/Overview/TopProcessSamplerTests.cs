using SystemToolkit.Core.Overview.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// Top 进程采样（B7①）判据。分两层：
/// ① **判据层**（选取 / 裁剪 / 稳定序 / Idle 过滤）——注入构造数据，完全离线、可精确断言，
///    实现改动必然变红（判据在本类里是 <c>internal static</c>，测试不复制规则）；
/// ② **真实采样层**——只断言"不抛 + 自洽 + 首拍不撒谎"，不依赖具体进程名/数值
///    （本机进程随时在变，钉名字的用例就是定时炸弹）。
/// </summary>
public class TopProcessSamplerTests
{
    private static ProcessUsageRow Row(int pid, string name, double? cpu, long workingSet) =>
        new(pid, name, cpu, workingSet);

    [Fact]
    public void SelectTopByCpu_RowsWithoutPercent_AreNotListed()
    {
        // 🔴 无基线（null）不得以 0% 冒充上榜："没有证据就不给数字"
        ProcessUsageRow[] rows = new[] { Row(1, "a", null, 100), Row(2, "b", 12.5, 100) };

        IReadOnlyList<ProcessUsageRow> top = TopProcessSampler.SelectTopByCpu(rows, 5);

        Assert.Single(top);
        Assert.Equal(2, top[0].Pid);
    }

    [Fact]
    public void SelectTopByCpu_OrdersDescending()
    {
        ProcessUsageRow[] rows = new[] { Row(1, "a", 1.0, 0), Row(2, "b", 9.0, 0), Row(3, "c", 5.0, 0) };

        IReadOnlyList<ProcessUsageRow> top = TopProcessSampler.SelectTopByCpu(rows, 3);

        Assert.Equal(new[] { 2, 3, 1 }, top.Select(r => r.Pid).ToArray());
    }

    [Fact]
    public void SelectTopByCpu_SameValue_IsStableByPid()
    {
        // 稳定序是为了让断言可复现：不稳定排序会让用例变成"看运气"
        ProcessUsageRow[] rows = new[] { Row(30, "c", 7.0, 0), Row(10, "a", 7.0, 0), Row(20, "b", 7.0, 0) };

        IReadOnlyList<ProcessUsageRow> top = TopProcessSampler.SelectTopByCpu(rows, 3);

        Assert.Equal(new[] { 10, 20, 30 }, top.Select(r => r.Pid).ToArray());
    }

    [Fact]
    public void SelectTopByCpu_TakesAtMostCount()
    {
        ProcessUsageRow[] rows = Enumerable.Range(1, 20).Select(i => Row(i, "p" + i, i, 0)).ToArray();

        Assert.Equal(5, TopProcessSampler.SelectTopByCpu(rows, 5).Count);
        Assert.Single(TopProcessSampler.SelectTopByCpu(rows, 1));
    }

    [Fact]
    public void SelectTopByMemory_OrdersDescending_AndStableByPid()
    {
        ProcessUsageRow[] rows = new[] { Row(5, "e", null, 100), Row(2, "b", null, 900), Row(7, "g", null, 900) };

        IReadOnlyList<ProcessUsageRow> top = TopProcessSampler.SelectTopByMemory(rows, 5);

        Assert.Equal(new[] { 2, 7, 5 }, top.Select(r => r.Pid).ToArray());
    }

    [Fact]
    public void IsCountedProcess_ExcludesIdlePid0()
    {
        // Idle(PID 0) 的「CPU 时间」是 CPU 空闲时间，纳入会直接霸榜
        Assert.False(TopProcessSampler.IsCountedProcess(0));
        Assert.True(TopProcessSampler.IsCountedProcess(4)); // System 是真实进程，保留
        Assert.True(TopProcessSampler.IsCountedProcess(1234));
    }

    [Fact]
    public void Sample_FirstCall_HasNoCpuBaseline_AndSaysSoHonestly()
    {
        var sampler = new TopProcessSampler();

        TopProcessSnapshot? first = sampler.Sample();

        Assert.NotNull(first);
        Assert.False(first!.HasCpuBaseline);
        // 没有基线 → CPU 榜必须为空，而不是一排 0%
        Assert.Empty(first.CpuTop);
        // 内存不依赖基线，首拍即可给出
        Assert.NotEmpty(first.MemoryTop);
        Assert.True(first.SampledCount > 0);
    }

    [Fact]
    public void Sample_OnceBaselineExists_CpuPercentsAreInRange_AndIdleIsAbsent()
    {
        var sampler = new TopProcessSampler();
        sampler.Sample(); // 建立基线

        TopProcessSnapshot? second = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        // 轮询等待"基线可达"，而不是固定 sleep 拍脑袋（时序用例纪律）。
        // 本用例同时钉住一条真实约束：**高频调用不会永久拿不到 CPU 数据**——
        // 采样器在窗口未满时保留基准、让窗口自然累积。若改回"每拍推进基准"，此处会超时变红。
        while (DateTime.UtcNow < deadline)
        {
            second = sampler.Sample();
            if (second?.HasCpuBaseline == true)
            {
                break;
            }

            Thread.Sleep(50);
        }

        Assert.True(second?.HasCpuBaseline == true, "间隔 > 0.25s 的第二次采样应已建立 CPU 基线");
        Assert.NotEmpty(second!.CpuTop);
        Assert.All(second.CpuTop, r => Assert.InRange(r.CpuPercent!.Value, 0, 100));
        Assert.All(second.MemoryTop, r => Assert.True(r.WorkingSetBytes >= 0));
        Assert.DoesNotContain(second.CpuTop, r => r.Pid == 0);
    }
}
