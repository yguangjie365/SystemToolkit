using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Tests.Drivers;

/// <summary>
/// pnputil / ElevatedHelper 输出语言无关分析器守卫。
/// 经验来源：RAPR AddResultRegex（Add 计数比对）+ 本项目 ElevatedHelper 分段协议（[exit N] 标记）。
/// </summary>
public class PnpUtilOutputAnalyzerTests
{
    [Fact]
    public void ParseAddDriverCounts_ChineseOutput_TakesLastTwoCounts()
    {
        const string output = """
            正在添加驱动程序包...
            驱动程序包添加成功。

            驱动程序包总数:      2
            已添加驱动程序包数:   2
            """;

        (int Total, int Added)? counts = PnpUtilOutputAnalyzer.ParseAddDriverCounts(output);

        Assert.NotNull(counts);
        Assert.Equal(2, counts.Value.Total);
        Assert.Equal(2, counts.Value.Added);
    }

    [Fact]
    public void ParseAddDriverCounts_EnglishOutput_Compatible()
    {
        const string output = """
            Adding the driver package...
            Driver package added successfully.

            Total driver packages: 3
            Added driver packages: 2
            """;

        (int Total, int Added)? counts = PnpUtilOutputAnalyzer.ParseAddDriverCounts(output);

        Assert.NotNull(counts);
        Assert.Equal(3, counts.Value.Total);
        Assert.Equal(2, counts.Value.Added);
    }

    [Fact]
    public void ParseAddDriverCounts_FullWidthColon_Compatible()
    {
        const string output = "驱动程序包总数：5\n已添加驱动程序包数：5\n";

        (int Total, int Added)? counts = PnpUtilOutputAnalyzer.ParseAddDriverCounts(output);

        Assert.NotNull(counts);
        Assert.Equal(5, counts.Value.Total);
    }

    [Fact]
    public void ParseAddDriverCounts_NoCounts_ReturnsNull()
    {
        Assert.Null(PnpUtilOutputAnalyzer.ParseAddDriverCounts(""));
        Assert.Null(PnpUtilOutputAnalyzer.ParseAddDriverCounts("任意文本无数字"));
        Assert.Null(PnpUtilOutputAnalyzer.ParseAddDriverCounts("只有一个计数: 42"));
    }

    [Fact]
    public void ExtractFailedSegments_MixedExitCodes_TakesOnlyFailedSegments()
    {
        const string output = """
            Microsoft PnP 工具

            [exit 1] pnputil pnputil /export-driver oem38.inf D:\b
            正在导出驱动程序包:   oem39.inf
            [exit 0] pnputil /export-driver oem39.inf D:\b
            [exit 0] pnputil /export-driver oem40.inf D:\b
            [exit -2] pnputil /delete-driver oem41.inf
            """;

        List<(int ExitCode, string Description)> failed = PnpUtilOutputAnalyzer.ExtractFailedSegments(output);

        Assert.Equal(2, failed.Count);
        Assert.Equal(1, failed[0].ExitCode);
        Assert.Contains("oem38.inf", failed[0].Description);
        Assert.Equal(-2, failed[1].ExitCode);
        Assert.Contains("oem41.inf", failed[1].Description);
    }

    [Fact]
    public void ExtractFailedSegments_AllSucceeded_ReturnsEmptyList()
    {
        const string output = "[exit 0] pnputil /export-driver oem1.inf D:\\b\n[exit 0] pnputil /export-driver oem2.inf D:\\b";

        Assert.Empty(PnpUtilOutputAnalyzer.ExtractFailedSegments(output));
    }

    [Fact]
    public void ExtractFailedSegments_EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(PnpUtilOutputAnalyzer.ExtractFailedSegments(""));
        Assert.Empty(PnpUtilOutputAnalyzer.ExtractFailedSegments(string.Empty));
    }
}
