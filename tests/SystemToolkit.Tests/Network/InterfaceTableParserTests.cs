using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// show interfaces 表格解析测试：真机 Win11 24H2 样例、名称含空格、
/// Loopback 过滤、表头/分隔线跳过。自旧工程移植，L15 英文命名。
/// </summary>
public class InterfaceTableParserTests
{
    private const string Sample = """
		Idx     Met         MTU          状态                名称
		---  ----------  ----------  ------------  ---------------------------
		  1          75  4294967295  connected     Loopback Pseudo-Interface 1
		 10          25        1500  disconnected  WLAN
		  8          25        1500  connected     LAN
		 15          25        1500  disconnected  本地连接* 3
		""";

    [Fact]
    public void RealSample_ParsesNonLoopbackInterfaces()
    {
        IReadOnlyList<InterfaceMetricInfo> result = InterfaceTableParser.Parse(Sample.Split('\n'));

        Assert.Equal(3, result.Count);
        Assert.Contains(result, m => m.Name == "LAN" && m.Metric == 25);
        Assert.Contains(result, m => m.Name == "WLAN" && m.Metric == 25);
    }

    [Fact]
    public void NameWithSpaces_ParsedAsWholeName()
    {
        IReadOnlyList<InterfaceMetricInfo> result = InterfaceTableParser.Parse(Sample.Split('\n'));

        Assert.Contains(result, m => m.Name == "本地连接* 3");
        Assert.DoesNotContain(result, m => m.Name.Contains("Loopback")); // 伪接口过滤
    }

    [Fact]
    public void HeaderAndSeparator_Skipped()
    {
        IReadOnlyList<InterfaceMetricInfo> result = InterfaceTableParser.Parse(new[] { "Idx Met MTU 状态 名称", "--- --- --- ---- ----" });

        Assert.Empty(result);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(InterfaceTableParser.Parse(Array.Empty<string>()));
    }
}
