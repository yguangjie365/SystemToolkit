using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// show global 输出解析器测试：中英双份真实样例逐字段钉死 + 降级路径
///（缺行 / 乱码 / 不认识的标签 → null「未知」，绝不猜值）。自旧工程移植，L15 英文命名。
/// </summary>
public class TcpSettingsParserTests
{
    // zh-CN（Win10 19041 实测形态；值大小写与空白宽度均按真实输出保留）
    private const string ZhSample = """
		查询活动状态...

		TCP 全局参数
		----------------------------------------------
		接收方缩放状态                : enabled
		窗口自动调节级别              : normal
		附加窗口拥塞控制提供程序      : none
		RFC 1323 窗口大小             : enabled
		积压(可扩展)                  : enabled(70)
		Syn 攻击保护                  : enabled
		Tcp Autotuning                : normal
		TCP Fast Open                 : enabled
		ECN 功能                      : disabled
		RSCTCP 关闭超时(秒)           : 300
		初始 RTO                      : 1000
		加载项拥塞控制提供程序        : default
		接收段合并状态                : enabled
		RFC 1323 时间戳               : allowed
		""";

    // en-US（同版本形态）
    private const string EnSample = """
		Querying active state...

		TCP Global Parameters
		----------------------------------------------
		Receive-Side Scaling State          : enabled
		Receive Window Auto-Tuning Level    : normal
		Add-On Congestion Control Provider  : none
		ECN Capability                      : disabled
		RFC 1323 Timestamps                 : disabled
		Initial RTO                         : 3000
		RSC (Receive Segment Coalescing)    : disabled
		Non Sack Rtt Resiliency             : disabled
		Max SYN Retransmissions             : 2
		""";

    [Fact]
    public void ChineseSample_ParsesThreeWritableFields()
    {
        TcpGlobalSettings settings = TcpSettingsParser.Parse(ZhSample.Split('\n'));

        Assert.Equal("normal", settings.AutoTuningLevel);
        Assert.True(settings.RssEnabled);
        Assert.False(settings.EcnEnabled);
    }

    [Fact]
    public void ReadOnlyFields_KeptAsRawValueNotBooleanized()
    {
        TcpGlobalSettings settings = TcpSettingsParser.Parse(ZhSample.Split('\n'));

        Assert.Equal("1000", settings.InitialRto);
        Assert.Equal("none", settings.CongestionProvider); // 原值展示（Win11 部分版本输出 none）
        Assert.Equal("enabled", settings.RscState);
        Assert.Equal("allowed", settings.Rfc1323Timestamps); // allowed ≠ enabled/disabled，原值保留
    }

    [Fact]
    public void EcnCapability_Win11Label_Parses()
    {
        // Win11 24H2 真机措辞是「ECN 功能」（2026-08-31 实测抓取）
        TcpGlobalSettings settings = TcpSettingsParser.Parse(ZhSample.Split('\n'));
        Assert.False(settings.EcnEnabled);
    }

    [Fact]
    public void EnglishSample_ParsesThreeWritableFields()
    {
        TcpGlobalSettings settings = TcpSettingsParser.Parse(EnSample.Split('\n'));

        Assert.Equal("normal", settings.AutoTuningLevel);
        Assert.True(settings.RssEnabled);
        Assert.False(settings.EcnEnabled);
    }

    [Fact]
    public void Values_CaseAndWhitespace_Normalized()
    {
        TcpGlobalSettings settings = TcpSettingsParser.Parse(new[]
        {
            "接收方缩放状态  :  ENABLED ",
            "ECN 能力:Disabled",
        });

        Assert.True(settings.RssEnabled);
        Assert.False(settings.EcnEnabled);
    }

    [Fact]
    public void MissingLines_FallBackToNull()
    {
        // 只有 RSS 一行：另两项必须为 null（未知），不能被误读成禁用
        TcpGlobalSettings settings = TcpSettingsParser.Parse(new[] { "接收方缩放状态 : enabled" });

        Assert.Null(settings.AutoTuningLevel);
        Assert.True(settings.RssEnabled);
        Assert.Null(settings.EcnEnabled);
    }

    [Fact]
    public void GarbageLines_AllNull_NoThrow()
    {
        TcpGlobalSettings settings = TcpSettingsParser.Parse(new[] { "??????", "", "###", "::::", "\t" });

        Assert.Null(settings.AutoTuningLevel);
        Assert.Null(settings.RssEnabled);
        Assert.Null(settings.EcnEnabled);
    }

    [Fact]
    public void UnknownValue_FallsBackToNull()
    {
        // 值既不是 enabled 也不是 disabled（如繁体 / 新版本措辞）：不猜
        TcpGlobalSettings settings = TcpSettingsParser.Parse(new[] { "接收方缩放状态 : 已启用" });

        Assert.Null(settings.RssEnabled);
    }

    [Fact]
    public void NetworkThrottlingIndex_AlwaysNull_RegistryFillsIt()
    {
        TcpGlobalSettings settings = TcpSettingsParser.Parse(ZhSample.Split('\n'));

        Assert.Null(settings.NetworkThrottlingIndex);
    }
}
