using SystemToolkit.Core.FileTransfer.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 一次性配对码服务契约测试（2026-09-06 批次二，自旧 Web 通道内联语义抽取为独立服务）：
/// 格式防误读、惰性轮换、一次性消费、过期拒绝、常量时间比较不抛异常。
/// </summary>
public class PairingServiceTests
{
    [Fact]
    public void CurrentCode_SixChars_FromConfusionFreeAlphabet()
    {
        var service = new PairingService();

        string code = service.CurrentCode;

        Assert.Equal(PairingService.CodeLength, code.Length);
        Assert.All(code, c => Assert.Contains(c, PairingService.CodeAlphabet));
        // 防人工误读：不出现 0/O/1/I
        Assert.DoesNotContain(code, c => "0O1I".Contains(c));
    }

    [Fact]
    public void CurrentCode_RotatesAfterLifetime()
    {
        var service = new PairingService(TimeSpan.FromMilliseconds(50));

        string first = service.CurrentCode;
        Assert.Equal(first, service.CurrentCode); // 有效期内稳定
        Thread.Sleep(80);

        string second = service.CurrentCode;
        Assert.NotEqual(first, second);
        Assert.True(service.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void TryConsume_CorrectCode_SucceedsOnceThenFails()
    {
        var service = new PairingService();
        string code = service.CurrentCode;

        Assert.True(service.TryConsume(code.ToLowerInvariant())); // 手机键盘自动小写也接受
        Assert.False(service.TryConsume(code));                   // 一次性：重放必败
        Assert.NotEqual(code, service.CurrentCode);               // 消费后自动轮换出新码
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]     // 长度不足
    [InlineData("1234567")]   // 超长
    [InlineData("ZZZZZZ")]    // 正确长度但错误
    public void TryConsume_WrongCode_RejectedWithoutSideEffect(string? wrong)
    {
        var service = new PairingService();
        string code = service.CurrentCode;

        Assert.False(service.TryConsume(wrong));
        Assert.Equal(code, service.CurrentCode); // 错误尝试不影响当前码
    }

    [Fact]
    public void TryConsume_ExpiredCode_Rejected()
    {
        var service = new PairingService(TimeSpan.FromMilliseconds(50));
        string code = service.CurrentCode;
        Thread.Sleep(80);

        Assert.False(service.TryConsume(code)); // 过期即拒（即使内容正确）
    }

    [Fact]
    public void TryConsume_ConcurrentSameCode_ExactlyOneSucceeds()
    {
        // 验收要点（设计 §8）：并发使用同一码，只能成功一次
        var service = new PairingService();
        string code = service.CurrentCode;
        int successes = 0;
        var barrier = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            barrier.Wait();
            if (service.TryConsume(code))
            {
                Interlocked.Increment(ref successes);
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        barrier.Set();
        threads.ForEach(t => t.Join());

        Assert.Equal(1, successes);
    }
}
