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

    /// <summary>
    /// 🟠 审查 v8-🟠-5：**并发访问不得丢条目、不得抛异常**。
    /// <para>
    /// 生产路径实证：<c>GameManagerViewModel</c> 用 <c>SemaphoreSlim(3)</c> + <c>Task.WhenAll</c>
    /// 并发 3 路调 <c>EnsureCoverFromCdnAsync</c>，而三路共享的是**同一个进程级实例**
    /// （<c>SteamService.DefaultCoverFailures</c> 静态 Lazy 单例）→ 同时命中
    /// <c>IsFailed</c> / <c>MarkFailed</c> / <c>ClearFailed</c>。
    /// </para>
    /// <para>
    /// 裸 <c>List</c> 的两处要害：① <c>EnsureLoaded</c> 的惰性初始化竞态——多线程同时看到
    /// <c>_logData is null</c>，各自 new 一个 <c>CoverFailureLog</c>，后写者覆盖前者 ⇒
    /// **已记的条目成批消失**；② <c>MarkFailed</c> 里的 <c>FirstOrDefault</c> 是 foreach，
    /// 别人正在 Add 时抛 <c>InvalidOperationException: Collection was modified</c>。
    /// </para>
    /// <para>
    /// <b>反向验证</b>：去掉 <c>CoverFailureMemory</c> 里的 <c>lock (_gate)</c>（并在
    /// <c>MarkFailed</c> 开头插一个 <c>Thread.Sleep(1)</c> 放大窗口）→ 本用例变红
    /// （条目数不足，或 errors 非空）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConcurrentAccess_DoesNotLoseEntriesNorThrow()
    {
        const int workers = 8;
        const int perWorker = 25; // 8 × 25 = 200 < MaxEntries(300)：不会被上限淘汰干扰判据
        CoverFailureMemory memory = NewMemory();
        DateTimeOffset now = DateTimeOffset.Now;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        using var start = new Barrier(workers);
        Task[] tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            try
            {
                start.SignalAndWait(); // 让各路尽可能同时冲进 EnsureLoaded
                for (int i = 0; i < perWorker; i++)
                {
                    memory.MarkFailed($"https://cdn.example/{w}-{i}.jpg", "HTTP 404", now);
                    memory.IsFailed($"https://cdn.example/{w}-{i}.jpg", now);
                    // 交叉读别的 worker 的条目：逼出"边遍历边改"
                    memory.IsFailed($"https://cdn.example/{(w + 1) % workers}-0.jpg", now);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(errors);

        var missing = new List<string>();
        for (int w = 0; w < workers; w++)
        {
            for (int i = 0; i < perWorker; i++)
            {
                if (!memory.IsFailed($"https://cdn.example/{w}-{i}.jpg", now))
                {
                    missing.Add($"{w}-{i}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"并发写入丢失 {missing.Count} 条（裸 List 下惰性加载竞态会成批覆盖）：{string.Join(",", missing.Take(10))}");
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
