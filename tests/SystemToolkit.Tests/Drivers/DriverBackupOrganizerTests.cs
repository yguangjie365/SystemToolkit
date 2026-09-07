using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// 备份目录分类整理器（RAPR GetDriversBackupFolderName 命名法）守卫。
/// v2（2026-09-05）：精确对位 API——调用方传（暂存内容目录 ↔ 包元数据），不再按目录名前缀猜测。
/// </summary>
public class DriverBackupOrganizerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stk_org_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string DriversRoot => Path.Combine(_dir, "Drivers");

    /// <summary>建暂存结构：Drivers\_stage_&lt;发布名基&gt;\&lt;pnputil产物目录名&gt;\，返回内容目录。</summary>
    private string CreateStageContent(string publishedBase, string contentDirName)
    {
        string content = Path.Combine(DriversRoot, "_stage_" + publishedBase, contentDirName);
        Directory.CreateDirectory(content);
        return content;
    }

    private static DriverPackage Pkg(
        string published, string original, string version,
        string className = "Net", List<string>? devices = null)
    {
        var p = new DriverPackage
        {
            PublishedName = published,
            OriginalName = original,
            Version = version,
            ClassName = className,
        };
        if (devices is not null)
        {
            p.DeviceNames = devices;
        }

        return p;
    }

    private static Dictionary<string, List<DriverDeviceMapper.DeviceBinding>> Bindings(
        string key, string deviceName, string? hwId = "PCI\\VEN_1")
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [key] = new() { new DriverDeviceMapper.DeviceBinding(deviceName, hwId) },
        };

    [Fact]
    public void StageDirFor_IsolatesByPublishedName()
    {
        Assert.Equal(
            Path.Combine(DriversRoot, "_stage_oem1"),
            DriverBackupOrganizer.StageDirFor(DriversRoot, "oem1.inf"));
        // 不同发布名（同名原始 INF 的两个版本）→ 不同暂存目录（D-3 隔离语义）
        Assert.NotEqual(
            DriverBackupOrganizer.StageDirFor(DriversRoot, "oem1.inf"),
            DriverBackupOrganizer.StageDirFor(DriversRoot, "oem2.inf"));
    }

    [Fact]
    public void Organize_RenamesByCategory_DeviceName_Version()
    {
        string content = CreateStageContent("oem1", "ibtusb");
        File.WriteAllText(Path.Combine(content, "ibtusb.inf"), "x");

        var entries = new List<(string, DriverPackage)>
        {
            (content, Pkg("oem1.inf", "ibtusb.inf", "22.120.0.4", "蓝牙")),
        };
        Dictionary<string, List<DriverDeviceMapper.DeviceBinding>> bindings = Bindings("oem1.inf", "英特尔(R) Wireless Bluetooth(R)");

        (int moved, int skipped) = DriverBackupOrganizer.OrganizeByCategory(DriversRoot, entries, bindings);

        Assert.Equal(1, moved);
        Assert.Equal(0, skipped);
        string target = Path.Combine(DriversRoot, "蓝牙", "英特尔(R) Wireless Bluetooth(R)_22.120.0.4");
        Assert.True(Directory.Exists(target));
        Assert.True(File.Exists(Path.Combine(target, "ibtusb.inf")));
        Assert.False(Directory.Exists(content));
        // 空暂存壳被清理（F3 契约）
        Assert.False(Directory.Exists(Path.Combine(DriversRoot, "_stage_oem1")));
    }

    [Fact]
    public void Organize_NoDeviceAssociation_UsesOriginalInfName()
    {
        string content = CreateStageContent("oem2", "orphan");

        var entries = new List<(string, DriverPackage)>
        {
            (content, Pkg("oem2.inf", "orphan.inf", "1.0.0.0", "显示适配器")),
        };

        (int moved, int _) = DriverBackupOrganizer.OrganizeByCategory(
            DriversRoot, entries,
            new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>());

        Assert.Equal(1, moved);
        Assert.True(Directory.Exists(Path.Combine(DriversRoot, "显示适配器", "orphan_1.0.0.0")));
    }

    [Fact]
    public void Organize_SameOriginalInfMultipleVersions_VersionSuffixDistinct_NoOverwrite()
    {
        string content1 = CreateStageContent("oem1", "nvlt");
        string content2 = CreateStageContent("oem2", "nvlt");

        var entries = new List<(string, DriverPackage)>
        {
            (content1, Pkg("oem1.inf", "nvlt.inf", "32.0.15.6102")),
            (content2, Pkg("oem2.inf", "nvlt.inf", "32.0.15.6614")),
        };

        (int moved, int skipped) = DriverBackupOrganizer.OrganizeByCategory(
            DriversRoot, entries,
            new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>());

        Assert.Equal(2, moved);
        Assert.Equal(0, skipped);
        Assert.True(Directory.Exists(Path.Combine(DriversRoot, "Net", "nvlt_32.0.15.6102")));
        Assert.True(Directory.Exists(Path.Combine(DriversRoot, "Net", "nvlt_32.0.15.6614")));
    }

    [Fact]
    public void Organize_IllegalCharacters_ReplacedWithUnderscores()
    {
        string content = CreateStageContent("oem3", "baddrv");

        var entries = new List<(string, DriverPackage)>
        {
            (content, Pkg("oem3.inf", "baddrv.inf", "2.0")),
        };
        Dictionary<string, List<DriverDeviceMapper.DeviceBinding>> bindings = Bindings("oem3.inf", "Bad:Device*Name?");

        DriverBackupOrganizer.OrganizeByCategory(DriversRoot, entries, bindings);

        // "Bad:Device*Name?" → 非法字符全部替换
        Assert.True(Directory.Exists(Path.Combine(DriversRoot, "Net", "Bad_Device_Name__2.0")));
    }

    [Fact]
    public void Sanitize_TruncationAndIllegalCharacters()
    {
        Assert.Equal("ab_c", DriverBackupOrganizer.Sanitize("ab:c", 10));
        string longName = new string('x', 100);
        Assert.Equal(70, DriverBackupOrganizer.Sanitize(longName, 70).Length);
        Assert.DoesNotContain(":", DriverBackupOrganizer.Sanitize("a:b|c<d>", 50));
    }

    [Fact]
    public void Organize_StagingDirMissing_Skips()
    {
        var entries = new List<(string, DriverPackage)>
        {
            (Path.Combine(DriversRoot, "_stage_oem9", "ghost"), Pkg("oem9.inf", "ghost.inf", "1.0")),
        };

        (int moved, int skipped) = DriverBackupOrganizer.OrganizeByCategory(
            DriversRoot, entries,
            new Dictionary<string, List<DriverDeviceMapper.DeviceBinding>>());

        Assert.Equal(0, moved);
        Assert.Equal(1, skipped);
    }
}
