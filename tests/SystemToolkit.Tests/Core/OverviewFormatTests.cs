using SystemToolkit.Core.Overview.Models;
using SystemToolkit.Core.Overview.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 本机概览模块的纯函数测试（不触碰 WMI / 注册表，保证测试快速稳定）。
/// </summary>
public class OverviewFormatTests
{
    [Theory]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700H", "12th Gen Intel Core i7-12700H")]
    [InlineData("AMD Ryzen(TM) 7 5800H", "AMD Ryzen 7 5800H")]
    [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4", "Intel Xeon CPU E5-2680 v4")]
    [InlineData("普通名字", "普通名字")]
    public void CleanCpuName_StripsTrademarkMarks(string raw, string expected)
    {
        Assert.Equal(expected, OverviewFormat.CleanCpuName(raw));
    }

    [Fact]
    public void MemorySticksSummary_SameCapacitySameSpeed_AggregatedSummary()
    {
        string? s = OverviewFormat.MemorySticksSummary("DDR5",
            new ulong[] { 16L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024 }, new[] { 4800, 4800 });
        Assert.Equal("DDR5 · 2 × 16 GB · 4800 MHz", s);
    }

    [Fact]
    public void MemorySticksSummary_VaryingCapacityAndSpeed_ListedSeparately()
    {
        const ulong g = 1024L * 1024 * 1024;
        string? s = OverviewFormat.MemorySticksSummary("DDR4", new ulong[] { 16 * g, 8 * g }, new[] { 3200, 5600 });
        Assert.Equal("DDR4 · 16 GB + 8 GB · 3200/5600 MHz", s);
    }

    [Fact]
    public void MemorySticksSummary_NoTypeNoSpeed_CapacityOnly()
    {
        const ulong g = 1024L * 1024 * 1024;
        string? s = OverviewFormat.MemorySticksSummary(null, new ulong[] { 8 * g }, Array.Empty<int>());
        Assert.Equal("1 × 8 GB", s);
    }

    [Fact]
    public void MemorySticksSummary_EmptyList_ReturnsNull()
    {
        Assert.Null(OverviewFormat.MemorySticksSummary("DDR5", Array.Empty<ulong>(), Array.Empty<int>()));
    }

    [Theory]
    [InlineData(true, 1099511627776ul, "SSD 1 TB")]                       // 单固态
    [InlineData(false, 2199023255552ul, "HDD 2 TB")]                      // 单机械
    [InlineData(true, 512110190592ul, "SSD 476.9 GB")]                    // 单固态（GB 级容量照实显示）
    public void StorageSummary_SingleMediaKind_TypePlusCapacity(bool isSsd, ulong size, string expected)
    {
        Assert.Equal(expected, OverviewFormat.StorageSummary([(isSsd, size)]));
    }

    [Fact]
    public void StorageSummary_MixedMediaKinds_GroupedAndJoined()
    {
        Assert.Equal("SSD 476.9 GB + HDD 931.5 GB",
            OverviewFormat.StorageSummary([(true, 512110190592ul), (false, 1000204886016ul)]));
    }

    [Fact]
    public void StorageSummary_SameMediaKindMultipleDrives_CapacitySummed()
    {
        Assert.Equal("HDD 4 TB",
            OverviewFormat.StorageSummary([(false, 2199023255552ul), (false, 2199023255552ul)]));
    }

    [Fact]
    public void StorageSummary_UnknownMediaKind_OmitsTypePrefix()
    {
        Assert.Equal("1.4 TB", OverviewFormat.StorageSummary([(null, 1531151570493ul)]));
    }

    [Fact]
    public void StorageSummary_EmptyList_ReturnsNull()
    {
        Assert.Null(OverviewFormat.StorageSummary([]));
    }

    [Theory]
    [InlineData(0ul, "0 B")]
    [InlineData(512ul, "512 B")]
    [InlineData(1024ul, "1 KB")]
    [InlineData(1536ul, "1.5 KB")]
    [InlineData(1048576ul, "1 MB")]
    [InlineData(3221225472ul, "3 GB")]
    [InlineData(1099511627776ul, "1 TB")]
    public void Bytes_FormatsToReadableUnit(ulong bytes, string expected)
    {
        Assert.Equal(expected, OverviewFormat.Bytes(bytes));
    }

    [Fact]
    public void BuildInstalledProgram_NormalEntry_BuildsCompleteProgram()
    {
        InstalledProgram? program = OverviewFormat.BuildInstalledProgram(
            displayName: " 7-Zip ",
            version: " 24.08 ",
            publisher: " Igor Pavlov ",
            installDate: "20240115",
            sizeBytes: 3L * 1024 * 1024 * 1024,
            systemComponent: 0,
            parentKeyName: null,
            releaseType: null);

        Assert.NotNull(program);
        Assert.Equal("7-Zip", program!.Name);
        Assert.Equal("24.08", program.Version);
        Assert.Equal("Igor Pavlov", program.Publisher);
        Assert.Equal("2024-01-15", program.InstalledOn);
        Assert.Equal("3 GB", program.Size);
    }

    [Theory]
    [InlineData(null)]            // 无显示名
    [InlineData("   ")]           // 空白显示名
    public void BuildInstalledProgram_NoDisplayName_ReturnsNull(string? displayName)
    {
        Assert.Null(OverviewFormat.BuildInstalledProgram(
            displayName, null, null, null, null, 0, null, null));
    }

    [Fact]
    public void BuildInstalledProgram_SystemComponent_ReturnsNull()
    {
        Assert.Null(OverviewFormat.BuildInstalledProgram(
            "Visual C++ Redistributable", null, null, null, null,
            systemComponent: 1, parentKeyName: null, releaseType: null));
    }

    [Fact]
    public void BuildInstalledProgram_MsiChildEntry_ReturnsNull()
    {
        Assert.Null(OverviewFormat.BuildInstalledProgram(
            "foo", null, null, null, null,
            systemComponent: 0, parentKeyName: "Parent", releaseType: null));
    }

    [Theory]
    [InlineData("Update")]
    [InlineData("Security Update")]
    [InlineData("Hotfix")]
    public void BuildInstalledProgram_WindowsUpdateEntry_ReturnsNull(string releaseType)
    {
        Assert.Null(OverviewFormat.BuildInstalledProgram(
            "Windows 11 Update KB123", null, "Microsoft Corporation", null, null,
            systemComponent: 0, parentKeyName: null, releaseType: releaseType));
    }

    [Theory]
    [InlineData(0ul, 0ul, null)]          // 总量为 0 → null
    [InlineData(512ul, 1024ul, "50%")]
    [InlineData(333ul, 1000ul, "33%")]
    [InlineData(1000ul, 1000ul, "100%")]
    public void Percent_ComputesPercentage(ulong used, ulong total, string? expected)
    {
        Assert.Equal(expected, OverviewFormat.Percent(used, total));
    }

    [Theory]
    [InlineData(0, 0, 0, "不足 1 分钟")]
    [InlineData(2, 0, 0, "2 天")]
    [InlineData(1, 3, 0, "1 天 3 小时")]
    [InlineData(0, 5, 12, "5 小时 12 分钟")]
    public void Uptime_FormatsToChineseDuration(int days, int hours, int minutes, string expected)
    {
        Assert.Equal(expected, OverviewFormat.Uptime(new TimeSpan(days, hours, minutes, 0)));
    }

    [Theory]
    [InlineData("Microsoft Windows 11 家庭中文版", "Windows 11 家庭中文版")]
    [InlineData("  Microsoft Windows 10 Pro ", "Windows 10 Pro")]
    [InlineData("Windows 10 专业版", "Windows 10 专业版")]
    [InlineData("   ", "")]
    public void TrimMicrosoftPrefix_StripsVendorPrefix(string input, string expected)
    {
        Assert.Equal(expected, OverviewFormat.TrimMicrosoftPrefix(input));
    }

    [Theory]
    [InlineData(0L, null)]                     // 速度为 0
    [InlineData(-1L, null)]                    // -1 表示未知速度
    [InlineData(1000L, "1 Kbps")]
    [InlineData(1_000_000_000L, "1 Gbps")]
    [InlineData(150_000_000L, "150 Mbps")]
    public void BitsPerSecond_FormatsNetworkSpeed(long bps, string? expected)
    {
        Assert.Equal(expected, OverviewFormat.BitsPerSecond(bps));
    }

    [Theory]
    [InlineData(20, "DDR")]
    [InlineData(24, "DDR3")]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(0, null)]
    [InlineData(999, null)]
    public void MemoryTypeName_MapsWmiMemoryType(int type, string? expected)
    {
        Assert.Equal(expected, OverviewFormat.MemoryTypeName(type));
    }

    [Theory]
    [InlineData(0ul, 0ul, null)]            // 任一为 0 → null
    [InlineData(80_000ul, 100_000ul, "80%")]
    [InlineData(45_000ul, 50_000ul, "90%")]
    [InlineData(30_000ul, 60_000ul, "50%")]
    public void BatteryHealth_ComputesHealthPercentage(ulong full, ulong design, string? expected)
    {
        // 【P3-9】电池死代码仅测试用例调用：屏蔽 Obsolete 警告（Release TreatWarningsAsErrors）
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal(expected, OverviewFormat.BatteryHealth(full, design));
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Theory]
    [InlineData("20241205000000.000000+000", "2024-12-05")]
    [InlineData(" 20241205000000.000000+000 ", "2024-12-05")]
    [InlineData("20241205", "2024-12-05")]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FormatWmiDate_CleansWmiDateRawText(string? raw, string? expected)
    {
        Assert.Equal(expected, OverviewFormat.FormatWmiDate(raw));
    }

    [Theory]
    [InlineData("The system has access to AC so no battery is being discharged. However, the battery is not necessarily charging.", "使用外接电源")]
    [InlineData("The battery is discharging.", "使用电池中")]
    [InlineData("The battery is charging.", "充电中")]
    [InlineData("The battery is fully charged.", "已充满")]
    [InlineData("Some unknown English description", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void BatteryStatusText_MapsEnglishStatusToChinese(string? description, string? expected)
    {
        // 【P3-9】电池死代码仅测试用例调用：屏蔽 Obsolete 警告（Release TreatWarningsAsErrors）
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal(expected, OverviewFormat.BatteryStatusText(description));
#pragma warning restore CS0618 // Type or member is obsolete
    }
}
