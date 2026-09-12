using SystemToolkit.Core.GameManager.Services;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// 非游戏条目过滤 + CDN 封面缓存守卫（网络下载本身不在单测范围，只测纯本地可判定部分）。
/// </summary>
public class SteamLibraryPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"stk_lib_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureCoverFromCdnAsync_CacheAlreadyExists_ReturnsCachedPathWithoutNetwork()
    {
        string cacheDir = Path.Combine(_root, "covers");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "570.jpg"), "cached-bytes");

        string? hit = await SteamService.EnsureCoverFromCdnAsync(cacheDir, 570);

        Assert.NotNull(hit);
        Assert.Equal(Path.Combine(cacheDir, "570.jpg"), hit);
    }

    [Fact]
    public async Task EnsureCoverFromCdnAsync_ZeroAppId_ReturnsNull()
    {
        Assert.Null(await SteamService.EnsureCoverFromCdnAsync(_root, 0));
    }

    [Fact]
    public async Task EnsureCoverFromCdnAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SteamService.EnsureCoverFromCdnAsync(_root, 570, cts.Token));
    }

    // ==================== 非游戏过滤（直接钉 Core 的实现，不再复述规则） ====================
    // ⚠️ 2026-09-13 改：此前这两条用例因判据是 private 而只能"按同一规则复述"，改实现不会变红
    // → 已把 IsNonGame 放宽为 internal，测试改为直接调用，判据变更必然被抓住。

    [Fact]
    public void SteamworksCommonRedistributables_IsNotAGame()
    {
        Assert.True(SteamService.IsNonGame(228983, "Steamworks Common Redistributables"));
        Assert.True(SteamService.IsNonGame(1, "Steamworks Common Redistributables")); // 名称判据单独成立
    }

    [Fact]
    public void NormalGame_IsKept()
    {
        Assert.False(SteamService.IsNonGame(1091500, "Cyberpunk 2077", "Game"));
        Assert.False(SteamService.IsNonGame(570, "Dota 2", "game")); // 大小写不敏感
    }

    /// <summary>
    /// 真机反馈的那 4 条：appinfo 里 <c>type=Config</c>，经 localconfig 混进库存后被显示成
    /// 「未安装的游戏」（Steam Client / Steam Screenshots / Steam Input Configs / Steam Game Notes）。
    /// </summary>
    [Theory]
    [InlineData(7u, "Steam Client")]
    [InlineData(760u, "Steam Screenshots")]
    [InlineData(241100u, "Steam Input Configs")]
    [InlineData(2371090u, "Steam Game Notes")]
    public void ConfigTypeEntries_AreNotGames(uint appId, string name)
    {
        Assert.True(SteamService.IsNonGame(appId, name, "Config"));
    }

    /// <summary>非 Game 的其它类型同样排除（工具 / 演示版 / 视频等都不是"游戏"）。</summary>
    [Theory]
    [InlineData("Tool")]
    [InlineData("tool")]
    [InlineData("Application")]
    [InlineData("Demo")]
    public void NonGameTypes_AreExcluded(string type)
    {
        Assert.True(SteamService.IsNonGame(12345, "Some App", type));
    }

    /// <summary>
    /// 🔴 类型未知（appinfo 读不到 / 条目缺失）时**判为是游戏**——
    /// 宁可多留一条可疑项，也不能因为 appinfo 解析失败就把整库判成非游戏。
    /// </summary>
    [Fact]
    public void UnknownType_IsKept()
    {
        Assert.False(SteamService.IsNonGame(1091500, "Cyberpunk 2077"));
        Assert.False(SteamService.IsNonGame(1091500, "Cyberpunk 2077", string.Empty));
    }
}
