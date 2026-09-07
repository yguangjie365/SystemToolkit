using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// Driver Store 分类守卫：同「原始名称」组内除最高版本外均为旧版本（清理候选）。
/// 设计红线前置：分类是清理的唯一依据源，判定错误会误删在用驱动。
/// </summary>
public class DriverStoreClassifierTests
{
    private static DriverPackage Pkg(string published, string original, string version, string? date = null)
    {
        return new DriverPackage
        {
            PublishedName = published,
            OriginalName = original,
            Version = version,
            Date = date is null ? null : DateTime.Parse(date),
        };
    }

    [Fact]
    public void Classify_SameOriginalNameMultipleVersions_OnlyLatestIsCurrent()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "rt640x64.inf", "10.1.2018.1", "2018-01-01"),
            Pkg("oem2.inf", "rt640x64.inf", "11.5.2024.1114", "2024-11-02"),
            Pkg("oem3.inf", "rt640x64.inf", "11.4.2024.999", "2024-10-01"),
        };

        DriverStoreClassifier.Classify(packages);

        Assert.Equal(DriverPackageState.OldVersion, packages[0].State);
        Assert.Equal(DriverPackageState.Current, packages[1].State); // 11.5 > 11.4（数值段比较）
        Assert.Equal(DriverPackageState.OldVersion, packages[2].State);
    }

    [Fact]
    public void Classify_EqualVersions_TakesLatestByDate()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "audio.inf", "1.0.0.0", "2020-01-01"),
            Pkg("oem2.inf", "audio.inf", "1.0.0.0", "2022-06-01"),
        };

        DriverStoreClassifier.Classify(packages);

        Assert.Equal(DriverPackageState.OldVersion, packages[0].State);
        Assert.Equal(DriverPackageState.Current, packages[1].State);
    }

    [Fact]
    public void Classify_VersionAndDateMissing_StillProducesSingleCurrent()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "mystery.inf", ""),
            Pkg("oem2.inf", "mystery.inf", ""),
        };

        DriverStoreClassifier.Classify(packages);

        Assert.Equal(1, packages.Count(p => p.State == DriverPackageState.Current));
        Assert.Equal(1, packages.Count(p => p.State == DriverPackageState.OldVersion));
    }

    [Fact]
    public void Classify_DifferentOriginalNames_FormIndependentGroups()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "nvlt.inf", "32.0.15.6102"),
            Pkg("oem2.inf", "rt640x64.inf", "11.5.2024.1114"),
        };

        DriverStoreClassifier.Classify(packages);

        Assert.All(packages, p => Assert.Equal(DriverPackageState.Current, p.State));
    }

    [Fact]
    public void Classify_OriginalNameMissing_FallsBackToPublishedNameGrouping()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("computrace.inf", "", "1.2.0.0"),
            Pkg("custom.inf", "", "2.0.0.0"),
        };

        DriverStoreClassifier.Classify(packages);

        Assert.All(packages, p => Assert.Equal(DriverPackageState.Current, p.State));
    }

    [Fact]
    public void Classify_EmptyCollection_DoesNotThrow()
    {
        DriverStoreClassifier.Classify(Array.Empty<DriverPackage>());
    }

    // ------------------------------------------------------------------
    // 🔴 启动关键防线（D-2 修复，2026-09-05）：第三方启动关键驱动一律系统关键、禁止删除
    // ------------------------------------------------------------------

    [Fact]
    public void BootCritical_ThirdPartyOldVersion_OverridesOldVersion_AsSystemCritical()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "iaStorvd.inf", "19.5.0.1"),  // 旧版本 + 启动关键
            Pkg("oem2.inf", "iaStorvd.inf", "19.5.1.2"),  // 最新，在用
        };
        packages[0].IsBootCritical = true;
        packages[1].DeviceNames = new List<string> { "Intel RST VMD Controller" };

        DriverStoreClassifier.Classify(packages, applyCleanupCategories: true);

        Assert.Equal(DriverPackageState.SystemCritical, packages[0].State);
        Assert.Equal(DriverPackageState.Current, packages[1].State);
    }

    [Fact]
    public void BootCritical_EffectiveInDefaultBranch_NotDependentOnCleanupSwitch()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "nvraid.inf", "1.0"),
        };
        packages[0].IsBootCritical = true;

        DriverStoreClassifier.Classify(packages); // applyCleanupCategories: false

        Assert.Equal(DriverPackageState.SystemCritical, packages[0].State);
    }

    [Fact]
    public void BootCritical_ThirdPartyWithoutDevices_StillSystemCritical()
    {
        var packages = new List<DriverPackage>
        {
            Pkg("oem1.inf", "orphan.inf", "3.0.0.0"),
        };
        packages[0].IsBootCritical = true;

        DriverStoreClassifier.Classify(packages, applyCleanupCategories: true);

        Assert.Equal(DriverPackageState.SystemCritical, packages[0].State);
    }
}
