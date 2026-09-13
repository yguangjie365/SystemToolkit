using SystemToolkit.Core.GameManager.Cover;
using SystemToolkit.Core.GameManager.Services;
using Xunit;

namespace SystemToolkit.Tests;

/// <summary>
/// 封面候选失败记忆（落地计划 B6）。
/// <para>
/// 要防的不是"某次取不到封面"，而是**每次刷新都把已知必然失败的候选重试一遍**
/// （候选链里旧域 404 是常态，每个 URL 10 秒超时）。同时必须守住两条反向红线：
/// 只记**确定性**失败、且**带 TTL**——否则网络抖动会变成一整天的封面缺失。
/// </para>
/// </summary>
public sealed class CoverFailureMemoryTests : IDisposable
{
    private readonly string _dir;

    private readonly string _file;

    public CoverFailureMemoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stk-coverfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "cover-failures.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    private CoverFailureMemory NewMemory(TimeSpan? ttl = null) => new(_file, ttl);

    [Fact]
    public void MarkFailed_ThenIsFailed_HitsWithinTtl()
    {
        CoverFailureMemory memory = NewMemory();
        DateTimeOffset now = DateTimeOffset.Now;

        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", now);

        Assert.True(memory.IsFailed("https://cdn.example/a.jpg", now.AddMinutes(30)));
    }

    [Fact]
    public void IsFailed_MissesOtherUrl()
    {
        CoverFailureMemory memory = NewMemory();
        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", DateTimeOffset.Now);

        Assert.False(memory.IsFailed("https://cdn.example/b.jpg", DateTimeOffset.Now));
    }

    [Fact]
    public void IsFailed_ExpiresAfterTtl_SoItRetriesLater()
    {
        // 🔴 永久拉黑会把"将来可能修好"的路堵死（CDN 换域本仓就经历过）
        CoverFailureMemory memory = NewMemory(TimeSpan.FromHours(6));
        DateTimeOffset now = DateTimeOffset.Now;
        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", now);

        Assert.True(memory.IsFailed("https://cdn.example/a.jpg", now.AddHours(5)));
        Assert.False(memory.IsFailed("https://cdn.example/a.jpg", now.AddHours(7)));
    }

    [Fact]
    public void IsFailed_EmptyUrl_IsNeverFailed()
    {
        CoverFailureMemory memory = NewMemory();

        Assert.False(memory.IsFailed("", DateTimeOffset.Now));
        Assert.False(memory.IsFailed(null, DateTimeOffset.Now));
    }

    [Fact]
    public void ClearFailed_ForgetsUrl()
    {
        CoverFailureMemory memory = NewMemory();
        DateTimeOffset now = DateTimeOffset.Now;
        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", now);

        memory.ClearFailed("https://cdn.example/a.jpg");

        Assert.False(memory.IsFailed("https://cdn.example/a.jpg", now));
    }

    [Fact]
    public void MarkFailed_IncrementsCountOnSameUrl()
    {
        CoverFailureMemory memory = NewMemory();
        DateTimeOffset now = DateTimeOffset.Now;

        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", now);
        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", now.AddMinutes(1));

        // 第二次仍只有一条记录；加载后的 Count 为 2
        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", now.AddMinutes(2));
        CoverFailureMemory reloaded = NewMemory();
        Assert.True(reloaded.IsFailed("https://cdn.example/a.jpg", now.AddMinutes(3)));

        string json = File.ReadAllText(_file);
        Assert.Contains("\"count\": 3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        NewMemory().MarkFailed("https://cdn.example/a.jpg", "HTTP 404", now);

        Assert.True(NewMemory().IsFailed("https://cdn.example/a.jpg", now));
    }

    [Fact]
    public void Prune_RemovesExpiredEntries()
    {
        CoverFailureMemory memory = NewMemory(TimeSpan.FromHours(1));
        DateTimeOffset now = DateTimeOffset.Now;
        memory.MarkFailed("https://cdn.example/old.jpg", "HTTP 404", now.AddHours(-5));
        memory.MarkFailed("https://cdn.example/new.jpg", "HTTP 404", now);

        Assert.Equal(1, memory.Prune(now));
        Assert.False(memory.IsFailed("https://cdn.example/old.jpg", now));
        Assert.True(memory.IsFailed("https://cdn.example/new.jpg", now));
    }

    [Fact]
    public void Entries_AreCappedAtMax()
    {
        CoverFailureMemory memory = NewMemory();
        DateTimeOffset now = DateTimeOffset.Now;
        for (int i = 0; i < CoverFailureLog.MaxEntries + 5; i++)
        {
            memory.MarkFailed($"https://cdn.example/{i}.jpg", "HTTP 404", now.AddSeconds(i));
        }

        string json = File.ReadAllText(_file);
        int count = json.Split("\"url\":").Length - 1;
        Assert.True(count <= CoverFailureLog.MaxEntries, $"实际 {count} 条，超出上限");
    }

    [Fact]
    public void CorruptFile_FallsBackToEmptyMemory()
    {
        File.WriteAllText(_file, "{ not json");

        CoverFailureMemory memory = NewMemory();

        Assert.False(memory.IsFailed("https://cdn.example/a.jpg", DateTimeOffset.Now));
    }

    [Fact]
    public void SaveFailure_DoesNotThrow()
    {
        // 失败记忆是"加速用缓存"，写不进去只该留痕：不该因为它而打断获取封面
        string fileAsDir = Path.Combine(_dir, "blocked");
        File.WriteAllText(fileAsDir, "x");
        var memory = new CoverFailureMemory(Path.Combine(fileAsDir, "nested", "cover-failures.json"));

        memory.MarkFailed("https://cdn.example/a.jpg", "HTTP 404", DateTimeOffset.Now);

        Assert.True(memory.IsFailed("https://cdn.example/a.jpg", DateTimeOffset.Now));
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(410, true)]
    [InlineData(403, false)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    [InlineData(408, false)]
    [InlineData(200, false)]
    public void ShouldRemember_OnlyDeterministicFailures(int statusCode, bool expected)
    {
        // 🔴 这条是这个类存在的一半意义：把"资源不在了"与"此刻网络不好"分开
        Assert.Equal(expected, CoverFailureMemory.ShouldRemember(statusCode));
    }

    [Fact]
    public void BuildCoverCdnUrls_OrderAndContent_ArePinned()
    {
        List<string> urls = SteamService.BuildCoverCdnUrls(2358720, null);

        Assert.Equal(4, urls.Count);
        Assert.Equal("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/2358720/header.jpg", urls[0]);
        Assert.Contains("library_600x900.jpg", urls[1], StringComparison.Ordinal);
        Assert.Contains("cdn.cloudflare.steamstatic.com", urls[3], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCoverCdnUrls_WithSuffix_PutsAppInfoPathFirst()
    {
        List<string> urls = SteamService.BuildCoverCdnUrls(2358720, "abc123/header.jpg");

        Assert.Equal(5, urls.Count);
        Assert.Equal(
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/2358720/abc123/header.jpg",
            urls[0]);
    }
}
