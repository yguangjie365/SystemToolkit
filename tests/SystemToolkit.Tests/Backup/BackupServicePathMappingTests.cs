using SystemToolkit.Core.Backup.Services;

namespace SystemToolkit.Tests.Backup;

/// <summary>
/// VSS 设备路径 ↔ 活动路径映射（2026-09-08 审查 A-1）：路径未规范化时
/// <c>StartsWith</c> 对混合分隔符（/ 与 \）会漏匹配，导致 manifest 记错活动路径。
/// 本组用例锁定规范化后的映射行为。仅 Windows 语义（备份跑在 Windows）。
/// </summary>
public class BackupServicePathMappingTests
{
    private const string DeviceRoot = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1";

    private static readonly Dictionary<string, string> ShadowMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"D:\Data"] = DeviceRoot,
    };

    [Fact]
    public void MapToShadow_BackslashPath_MapsToDevicePath()
    {
        string result = BackupService.MapToShadow(@"D:\Data\sub\a.txt", ShadowMap);

        Assert.StartsWith(DeviceRoot, result, StringComparison.Ordinal);
        Assert.EndsWith(@"sub\a.txt", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MapToShadow_MixedSlashes_StillMaps()
    {
        // 混合分隔符：规范化前会漏匹配（审查 A-1 的场景）
        string result = BackupService.MapToShadow("D:/Data/sub/a.txt", ShadowMap);

        Assert.StartsWith(DeviceRoot, result, StringComparison.Ordinal);
        Assert.EndsWith(@"sub\a.txt", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MapToShadow_TrailingSeparatorOnRoot_NoDoubleSeparator()
    {
        string result = BackupService.MapToShadow(@"D:\Data\", ShadowMap);

        Assert.DoesNotContain(DeviceRoot + @"\\", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MapToLive_DevicePath_ReturnsLivePath()
    {
        string result = BackupService.MapToLive(DeviceRoot + @"\sub\a.txt", ShadowMap);

        Assert.Equal(@"D:\Data\sub\a.txt", result);
    }

    [Fact]
    public void MapToLive_UnknownPath_ReturnsAsIs()
        => Assert.Equal(@"E:\Other\a.txt", BackupService.MapToLive(@"E:\Other\a.txt", ShadowMap));

    [Fact]
    public void MapToShadow_EmptyMap_ReturnsAsIs()
    {
        var empty = new Dictionary<string, string>();

        Assert.Equal(@"D:\Data\a.txt", BackupService.MapToShadow(@"D:\Data\a.txt", empty));
    }
}
