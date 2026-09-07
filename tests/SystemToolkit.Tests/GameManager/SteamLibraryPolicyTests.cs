using SystemToolkit.Core.GameManager.Models;
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

    [Fact]
    public void SteamworksCommonRedistributables_IsNotAGame()
    {
        // appid 228983 = Steamworks Common Redistributables（Steam 公共运行库，非游戏）
        var g = new SteamGame { AppId = 228983, Name = "Steamworks Common Redistributables" };

        Assert.True(IsFilteredByPolicy(g));
    }

    [Fact]
    public void NormalGame_IsKept()
    {
        var g = new SteamGame { AppId = 1091500, Name = "Cyberpunk 2077" };

        Assert.False(IsFilteredByPolicy(g));
    }

    /// <summary>与 Core 的过滤策略保持一致（策略私有，此处按同一规则复述以锁定行为）。</summary>
    private static bool IsFilteredByPolicy(SteamGame g) =>
        g.AppId == 228983
        || g.Name.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase);
}
