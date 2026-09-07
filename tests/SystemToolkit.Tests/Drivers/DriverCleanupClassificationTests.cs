using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// FR-06 四类清理分类（applyCleanupCategories: true 分支）：
/// 优先级「系统关键 &gt; 正在使用 &gt; 旧版本 &gt; 无设备关联」。
/// </summary>
public class DriverCleanupClassificationTests
{
    private static DriverPackage Pkg(
        string published, string original, string version,
        List<string>? devices = null, bool thirdParty = true)
    {
        var p = new DriverPackage
        {
            PublishedName = published,
            OriginalName = original,
            Version = version,
        };
        if (!thirdParty)
        {
            // 收件箱（非 oem 前缀）由 PublishedName 决定 IsThirdParty，用非 oem 名模拟
        }
        if (devices is not null)
        {
            p.DeviceNames = devices;
        }

        return p;
    }

    [Fact]
    public void Classify_InboxDriver_SystemCritical_EvenIfOldestInGroup()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("computrace.inf", "computrace.inf", "1.0.0.0", thirdParty: false),
        };

        DriverStoreClassifier.Classify(packages, applyCleanupCategories: true);

        Assert.Equal(DriverPackageState.SystemCritical, packages[0].State);
    }

    [Fact]
    public void Classify_ThirdPartyWithDeviceAssociation_StaysCurrent()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "nvlt.inf", "32.0.15.6102", devices: new List<string> { "NVIDIA RTX" }),
        };

        DriverStoreClassifier.Classify(packages, applyCleanupCategories: true);

        Assert.Equal(DriverPackageState.Current, packages[0].State);
    }

    [Fact]
    public void Classify_ThirdPartyOlderInGroup_MarkedOldVersionEvenWithoutDevices()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "audio.inf", "1.0.0.0"), // 旧，无设备
            Pkg("oem2.inf", "audio.inf", "2.0.0.0"), // 最新，有设备
        };
        packages[0].DeviceNames = new List<string>();
        packages[1].DeviceNames = new List<string> { "Realtek Audio" };

        DriverStoreClassifier.Classify(packages, applyCleanupCategories: true);

        Assert.Equal(DriverPackageState.OldVersion, packages[0].State);
        Assert.Equal(DriverPackageState.Current, packages[1].State);
    }

    [Fact]
    public void Classify_ThirdPartyLatestWithoutDevices_NoDeviceAssociation()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "orphan.inf", "3.0.0.0"),
        };

        DriverStoreClassifier.Classify(packages, applyCleanupCategories: true);

        Assert.Equal(DriverPackageState.NoDeviceAssociation, packages[0].State);
    }

    [Fact]
    public void Classify_Priority_SystemCriticalOverridesAll()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("inbox.inf", "inbox.inf", "9.9.9.9", devices: new List<string> { "某设备" }, thirdParty: false),
        };

        DriverStoreClassifier.Classify(packages, applyCleanupCategories: true);

        Assert.Equal(DriverPackageState.SystemCritical, packages[0].State);
    }

    [Fact]
    public void Classify_DefaultBranch_KeepsLegacySemantics_NoFourCategoryMerge()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "orphan.inf", "3.0.0.0"), // 无设备但默认分支不改判
        };

        DriverStoreClassifier.Classify(packages); // 不传开关

        Assert.Equal(DriverPackageState.Current, packages[0].State);
    }
}
