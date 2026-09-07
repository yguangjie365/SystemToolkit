using System.Globalization;
using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// pnputil /enum-drivers 输出解析守卫。
/// 中文系统输出为本地化标签（发布名称/原始名称/类别/提供程序…），英文为 Published Name 等——
/// 解析按关键词匹配 + .inf 值判定，两种语系与折叠空行都必须正确。
/// </summary>
public class PnpUtilEnumParserTests
{
    private const string ZhFixture = """
        发布名称:            oem12.inf
        原始名称:            nvlt.inf
        类型:                驱动
        类别:                显示适配器
        类 GUID:             {4d36e968-e325-11ce-bfc1-08002be10318}
        提供程序:            NVIDIA
        日期:                2026/8/4
        版本:                32.0.15.6102

        发布名称:            oem15.inf
        原始名称:            rt640x64.inf
        类型:                驱动
        类别:                网络适配器
        类 GUID:             {4d36e972-e325-11ce-bfc1-08002be10318}
        提供程序:            Realtek
        日期:                2024/11/2
        版本:                11.5.2024.1114

        发布名称:            computrace.inf
        原始名称:            computrace.inf
        类型:                驱动
        类别:                系统设备
        类 GUID:             {4d36e97d-e325-11ce-bfc1-08002be10318}
        提供程序:            Absolute Software
        日期:                2022/1/1
        版本:                1.2.0.0
        """;

    private const string EnFixture = """
        Published Name:     oem7.inf
        Original Name:      hdaudvst.inf
        Type:               Driver
        Class Name:         Sound, video and game controllers
        Class GUID:         {4d36e96c-e325-11ce-bfc1-08002be10318}
        Provider Name:      Realtek
        Date:               3/21/2026
        Version:            6.0.10007.1
        """;

    [Fact]
    public void Parse_ChineseOutput_AllFieldsMatched()
    {
        List<DriverPackage> packages = PnpUtilService.ParseEnumOutput(ZhFixture, CultureInfo.InvariantCulture);

        Assert.Equal(3, packages.Count);

        DriverPackage gpu = packages[0];
        Assert.Equal("oem12.inf", gpu.PublishedName);
        Assert.Equal("nvlt.inf", gpu.OriginalName);
        Assert.Equal("驱动", gpu.Type);
        Assert.Equal("显示适配器", gpu.ClassName);
        Assert.Equal("{4d36e968-e325-11ce-bfc1-08002be10318}", gpu.ClassGuid);
        Assert.Equal("NVIDIA", gpu.Provider);
        Assert.Equal(new DateTime(2026, 8, 4), gpu.Date);
        Assert.Equal("32.0.15.6102", gpu.Version);
        Assert.True(gpu.IsThirdParty);
    }

    [Fact]
    public void Parse_EnglishOutput_AllFieldsMatched()
    {
        List<DriverPackage> packages = PnpUtilService.ParseEnumOutput(EnFixture, new CultureInfo("en-US"));

        DriverPackage pkg = Assert.Single(packages);
        Assert.Equal("oem7.inf", pkg.PublishedName);
        Assert.Equal("hdaudvst.inf", pkg.OriginalName);
        Assert.Equal("Driver", pkg.Type);
        Assert.Equal("Sound, video and game controllers", pkg.ClassName);
        Assert.Equal("Realtek", pkg.Provider);
        Assert.Equal(new DateTime(2026, 3, 21), pkg.Date);
        Assert.Equal("6.0.10007.1", pkg.Version);
    }

    [Fact]
    public void Parse_InboxDriver_NotCountedAsThirdParty()
    {
        List<DriverPackage> packages = PnpUtilService.ParseEnumOutput(ZhFixture, CultureInfo.InvariantCulture);
        Assert.False(packages[2].IsThirdParty); // computrace.inf 原生名
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsEmptyListDoesNotThrow()
    {
        Assert.Empty(PnpUtilService.ParseEnumOutput("", CultureInfo.InvariantCulture));
        Assert.Empty(PnpUtilService.ParseEnumOutput("\r\n  \r\n", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parse_UnrelatedLinesWithValues_NoGhostPackages()
    {
        const string noisy = """
            处理中: 100
            某个无冒号行
            发布名称:            oem1.inf
            版本:                1.0.0.0
            """;

        List<DriverPackage> packages = PnpUtilService.ParseEnumOutput(noisy, CultureInfo.InvariantCulture);
        DriverPackage pkg = Assert.Single(packages);
        Assert.Equal("oem1.inf", pkg.PublishedName);
    }

    [Fact]
    public void Parse_FullWidthColonSeparator_FieldsMatched()
    {
        // 用户环境实测症状（2026-09-04）：分隔符/编码差异导致除发布名外全部解析失败
        const string fullwidth = """
            发布名称：            oem3.inf
            原始名称：            rt640x64.inf
            类型：                驱动
            类别：                网络适配器
            类 GUID：             {4d36e972-e325-11ce-bfc1-08002be10318}
            提供程序：            Realtek
            日期：                2024/11/2
            版本：                11.5.2024.1114
            """;

        DriverPackage pkg = Assert.Single(PnpUtilService.ParseEnumOutput(fullwidth, CultureInfo.InvariantCulture));
        Assert.Equal("oem3.inf", pkg.PublishedName);
        Assert.Equal("rt640x64.inf", pkg.OriginalName);
        Assert.Equal("网络适配器", pkg.ClassName);
        Assert.Equal("Realtek", pkg.Provider);
        Assert.Equal("11.5.2024.1114", pkg.Version);
    }

    [Fact]
    public void Parse_GarbledLabels_EightFieldBlockFallsBackToPositionOrder()
    {
        // 编码失配时中文标签变乱码、关键词全部失配——pnputil 字段顺序跨语言稳定，必须兜底
        const string mojibake = """
            ·¢²¼Ãû³Æ:            oem9.inf
            Ô­Ê¼Ãû³Æ:            nvlt.inf
            ÀàÐÍ:                Driver
            Àà±ð:                Display adapters
            Àà GUID:             {4d36e968-e325-11ce-bfc1-08002be10318}
            Ìá¹©³ÌÐò:            NVIDIA
            ÈÕÆÚ:                2026/8/4
            °æ±¾:                32.0.15.6102
            """;

        DriverPackage pkg = Assert.Single(PnpUtilService.ParseEnumOutput(mojibake, CultureInfo.InvariantCulture));
        Assert.Equal("oem9.inf", pkg.PublishedName);
        Assert.Equal("nvlt.inf", pkg.OriginalName);
        Assert.Equal("Display adapters", pkg.ClassName);
        Assert.Equal("{4d36e968-e325-11ce-bfc1-08002be10318}", pkg.ClassGuid);
        Assert.Equal("NVIDIA", pkg.Provider);
        Assert.Equal(new DateTime(2026, 8, 4), pkg.Date);
        Assert.Equal("32.0.15.6102", pkg.Version);
    }

    [Fact]
    public void Parse_FewerThan8FieldLines_DoesNotMisusePositionalFallback()
    {
        // 少于 8 行时位置序不可信，只能用关键词命中的部分 + Unknown 兜底
        const string partial = """
            发布名称:            oem5.inf
            版本:                2.0.0.0
            """;

        DriverPackage pkg = Assert.Single(PnpUtilService.ParseEnumOutput(partial, CultureInfo.InvariantCulture));
        Assert.Equal("oem5.inf", pkg.PublishedName);
        Assert.Equal("2.0.0.0", pkg.Version);
        Assert.Equal("", pkg.OriginalName);
        Assert.Equal("", pkg.Provider);
    }

    [Fact]
    public void Parse_Win11NewFormat_VersionLineSplitsDateAndVersion()
    {
        // 2026-09-04 本机实测（Win11 26100 pnputil）：无独立日期行，
        // 「驱动程序版本」= "日期 版本号"；另有扩展ID/签名者/属性/WHCP 等额外行；类名标签为「类名」。
        const string win11 = """
            发布名称:     oem38.inf
            原始名称:      acpivpc.inf
            提供程序名称:      Lenovo
            类名:         System
            类 GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
            驱动程序版本:     08/14/2025 15.11.30.11
            签名者姓名:        Microsoft Windows Hardware Compatibility Publisher
            属性:        Universal
            WHCP 版本:      未知
            """;

        DriverPackage pkg = Assert.Single(PnpUtilService.ParseEnumOutput(win11, CultureInfo.InvariantCulture));
        Assert.Equal("oem38.inf", pkg.PublishedName);
        Assert.Equal("acpivpc.inf", pkg.OriginalName);
        Assert.Equal("Lenovo", pkg.Provider);
        Assert.Equal("System", pkg.ClassName);          // 「类名」标签必须命中，否则全部塌进"未分类"
        Assert.Equal(new DateTime(2025, 8, 14), pkg.Date); // 日期从「驱动程序版本」行拆出
        Assert.Equal("15.11.30.11", pkg.Version);          // 版本列不得再显示日期
        Assert.True(pkg.IsThirdParty);
    }

    [Fact]
    public void Parse_LegacyFormatWithSeparateDateLine_VersionLineNotMisSplit()
    {
        const string legacy = """
            发布名称:            oem2.inf
            原始名称:            rt640x64.inf
            类型:                驱动
            类别:                网络适配器
            类 GUID:             {4d36e972-e325-11ce-bfc1-08002be10318}
            提供程序:            Realtek
            日期:                2024/11/2
            版本:                11.5.2024.1114
            """;

        DriverPackage pkg = Assert.Single(PnpUtilService.ParseEnumOutput(legacy, CultureInfo.InvariantCulture));
        Assert.Equal(new DateTime(2024, 11, 2), pkg.Date);
        Assert.Equal("11.5.2024.1114", pkg.Version);
    }
}
