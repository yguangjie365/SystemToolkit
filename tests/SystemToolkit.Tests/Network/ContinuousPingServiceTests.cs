using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 持续 ping 服务测试（2026-09-06 新增服务）。探测目标用回环地址 127.0.0.1——
/// 不依赖外网、无权限要求、必回包；取消即停与序号递增是本服务的核心契约。
/// </summary>
public class ContinuousPingServiceTests
{
    /// <summary>同步进度收集器：不经 SyncContext 投递，规避 Progress&lt;T&gt; 的异步竞态。</summary>
    private sealed class CollectingProgress : IProgress<PingSample>
    {
        public List<PingSample> Samples { get; } = new();

        public Action<PingSample>? OnSample { get; set; }

        public void Report(PingSample value)
        {
            Samples.Add(value);
            OnSample?.Invoke(value);
        }
    }

    [Fact]
    public async Task RunAsync_NonPositiveInterval_Throws()
    {
        var service = new ContinuousPingService();
        var progress = new CollectingProgress();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RunAsync("127.0.0.1", intervalMs: 0, progress, cts.Token));
    }

    [Fact]
    public async Task RunAsync_Loopback_SequentialSamples_StopsOnCancel()
    {
        var service = new ContinuousPingService();
        var progress = new CollectingProgress();
        using var cts = new CancellationTokenSource();
        progress.OnSample = _ =>
        {
            if (progress.Samples.Count >= 3)
            {
                cts.Cancel(); // 第 3 包后取消 → 循环应干净退出
            }
        };

        await service.RunAsync("127.0.0.1", intervalMs: 10, progress, cts.Token);

        Assert.Equal(3, progress.Samples.Count);
        Assert.Equal(new[] { 1, 2, 3 }, progress.Samples.Select(s => s.Seq));
        Assert.All(progress.Samples, s =>
        {
            Assert.True(s.Success, "回环地址必回包");
            Assert.Null(s.Error);
            Assert.NotNull(s.LatencyMs);
        });
    }

    [Fact]
    public async Task RunAsync_CancelBeforeStart_ReportsNothing()
    {
        var service = new ContinuousPingService();
        var progress = new CollectingProgress();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await service.RunAsync("127.0.0.1", intervalMs: 10, progress, cts.Token);

        Assert.Empty(progress.Samples);
    }
}
