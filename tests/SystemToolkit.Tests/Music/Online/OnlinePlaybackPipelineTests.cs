using SystemToolkit.Core.Music.Online;
using SystemToolkit.Infrastructure.Music.Online;

namespace SystemToolkit.Tests.Music.Online;

/// <summary>OM-4 跳过策略（纯函数）：原因分类 + 连续上限。</summary>
public class OnlineSkipPolicyTests
{
    [Fact]
    public void Describe_VipTrack_ProducesExplicitVipMessage()
    {
        var result = new OnlineSongUrlResult
        {
            Playable = false,
            Reason = "url_unavailable",
            Fee = 1,
            Message = "该歌曲可能需要 VIP 或登录",
        };

        string text = OnlineSkipPolicy.Describe(result);

        Assert.Contains("VIP", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("url_unavailable", 0, "无法获取播放地址")]
    [InlineData("trial_only", 8, "试听")]
    [InlineData("invalid_provider", 0, "未知音乐来源")]
    public void Describe_ClassifiesKnownReasons(string reason, int fee, string expectedFragment)
    {
        var result = new OnlineSongUrlResult { Playable = false, Reason = reason, Fee = fee };

        string text = OnlineSkipPolicy.Describe(result);

        Assert.Contains(expectedFragment, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_PlayableResult_ReturnsEmpty()
    {
        var result = new OnlineSongUrlResult { Playable = true, Url = "https://x", Level = "exhigh" };

        Assert.Equal(string.Empty, OnlineSkipPolicy.Describe(result));
    }

    [Fact]
    public void ShouldStop_BelowCap_ReturnsFalse()
    {
        for (int i = 1; i < OnlineSkipPolicy.MaxConsecutiveSkips; i++)
        {
            Assert.False(OnlineSkipPolicy.ShouldStop(i, out _), $"count={i} 不应停止");
        }
    }

    [Fact]
    public void ShouldStop_AtCap_ReturnsTrueWithUserVisibleSummary()
    {
        bool stop = OnlineSkipPolicy.ShouldStop(OnlineSkipPolicy.MaxConsecutiveSkips, out string? message);

        Assert.True(stop);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("停止", message, StringComparison.Ordinal);
    }
}

/// <summary>OM-4 URL 解析器：平台分发 + Cookie 注入 + 异常收敛（委托注入，无网络）。</summary>
public class OnlineUrlResolverTests
{
    private sealed class MemoryCredentialStore : IOnlineCredentialStore
    {
        public Dictionary<OnlineProvider, string> Cookies { get; } = [];

        public string? GetCookie(OnlineProvider provider) =>
            Cookies.TryGetValue(provider, out string? v) ? v : null;

        public void SetCookie(OnlineProvider provider, string cookie) => Cookies[provider] = cookie;

        public void Clear(OnlineProvider provider) => Cookies.Remove(provider);
    }

    private static OnlineTrack MakeTrack(OnlineProvider provider, string id, string? mid = null) => new()
    {
        Provider = provider,
        Id = id,
        Mid = mid,
        Name = "测试曲",
        Artist = "测试艺术家",
    };

    [Fact]
    public async Task ResolveAsync_NeteaseTrack_PassesIdQualityAndCookie()
    {
        var store = new MemoryCredentialStore();
        store.SetCookie(OnlineProvider.NetEase, "MUSIC_U=secret");

        string? seenId = null, seenQuality = null, seenCookie = null;
        var resolver = new OnlineUrlResolver(
            (id, quality, cookie, _) =>
            {
                seenId = id;
                seenQuality = quality;
                seenCookie = cookie;
                return Task.FromResult(new OnlineSongUrlResult { Playable = true, Url = "https://audio", Level = quality });
            },
            (_, _, _, _) => throw new InvalidOperationException("QQ 客户端不应被调用"),
            store,
            new NoopLogger());

        OnlineSongUrlResult result = await resolver.ResolveAsync(MakeTrack(OnlineProvider.NetEase, "33894312"), "lossless");

        Assert.True(result.Playable);
        Assert.Equal("33894312", seenId);
        Assert.Equal("lossless", seenQuality);
        Assert.Equal("MUSIC_U=secret", seenCookie);
    }

    [Fact]
    public async Task ResolveAsync_QqTrack_PrefersMidAndInjectsQqCookie()
    {
        var store = new MemoryCredentialStore();
        store.SetCookie(OnlineProvider.QQMusic, "uin=10000; qqmusic_key=abc");

        string? seenMid = null, seenCookie = null;
        var resolver = new OnlineUrlResolver(
            (_, _, _, _) => throw new InvalidOperationException("网易客户端不应被调用"),
            (mid, mediaMid, cookie, _) =>
            {
                seenMid = mid;
                seenCookie = cookie;
                return Task.FromResult(new OnlineSongUrlResult { Playable = true, Url = "https://audio", Level = "standard" });
            },
            store,
            new NoopLogger());

        OnlineSongUrlResult result = await resolver.ResolveAsync(MakeTrack(OnlineProvider.QQMusic, "fallbackId", mid: "realMid"), "lossless");

        Assert.True(result.Playable);
        Assert.Equal("realMid", seenMid); // Mid 优先于 Id
        Assert.Equal("uin=10000; qqmusic_key=abc", seenCookie);
    }

    [Fact]
    public async Task ResolveAsync_ClientThrows_ConvergesToUnplayableErrorResult()
    {
        var resolver = new OnlineUrlResolver(
            (_, _, _, _) => throw new HttpRequestException("网络断开"),
            (_, _, _, _) => throw new InvalidOperationException(),
            new MemoryCredentialStore(),
            new NoopLogger());

        OnlineSongUrlResult result = await resolver.ResolveAsync(MakeTrack(OnlineProvider.NetEase, "42"), "exhigh");

        // 🔴 网络异常不得抛给 VM——收敛为不可播结果，由跳过策略统一处理
        Assert.False(result.Playable);
        Assert.Equal("error", result.Reason);
        Assert.Contains("网络断开", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_UnknownProvider_ReturnsInvalidProvider()
    {
        var resolver = new OnlineUrlResolver(
            (_, _, _, _) => throw new InvalidOperationException(),
            (_, _, _, _) => throw new InvalidOperationException(),
            new MemoryCredentialStore(),
            new NoopLogger());

        OnlineSongUrlResult result = await resolver.ResolveAsync(MakeTrack(OnlineProvider.Local, "x"), "exhigh");

        Assert.False(result.Playable);
        Assert.Equal("invalid_provider", result.Reason);
    }
}
