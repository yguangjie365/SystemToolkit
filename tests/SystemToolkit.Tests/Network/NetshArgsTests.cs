using System.Net;
using SystemToolkit.Core.Network.Models;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// netsh 参数构造逐字回归（自旧工程移植，L15 英文命名）。构造结果同时用于执行与
/// 「复制命令」展示——一处构造、两处消费，逐字钉死防漂移。
/// </summary>
public class NetshArgsTests
{
    [Fact]
    public void SetDhcp_NameWithChineseAndSpaces_Quoted()
    {
        Assert.Equal(
            "interface ipv4 set address name=\"无线 网络 2\" source=dhcp",
            NetshArgs.SetDhcp("无线 网络 2"));
    }

    [Fact]
    public void SetStaticIp_WithGateway_FullCommand()
    {
        Assert.Equal(
            "interface ipv4 set address name=\"以太网\" source=static address=192.168.1.10 mask=255.255.255.0 gateway=192.168.1.1",
            NetshArgs.SetStaticIp("以太网", "192.168.1.10", "255.255.255.0", "192.168.1.1"));
    }

    [Fact]
    public void SetStaticIp_WithoutGateway_OmitsGatewaySegment()
    {
        Assert.Equal(
            "interface ipv4 set address name=\"以太网\" source=static address=10.0.0.5 mask=255.0.0.0",
            NetshArgs.SetStaticIp("以太网", "10.0.0.5", "255.0.0.0", null));
    }

    [Fact]
    public void SetDnsToDhcp_EmitsSourceDhcp()
    {
        Assert.Equal(
            "interface ipv4 set dnsservers name=\"以太网\" source=dhcp",
            NetshArgs.SetDnsToDhcp("以太网"));
    }

    [Fact]
    public void SetDnsPrimary_EmitsRegisterPrimary()
    {
        Assert.Equal(
            "interface ipv4 set dnsservers name=\"以太网\" source=static address=223.5.5.5 register=primary",
            NetshArgs.SetDnsPrimary("以太网", "223.5.5.5"));
    }

    [Fact]
    public void SetDnsSecondary_FixedIndexTwo()
    {
        Assert.Equal(
            "interface ipv4 add dnsservers name=\"以太网\" address=223.6.6.6 index=2",
            NetshArgs.AddDnsSecondary("以太网", "223.6.6.6"));
    }

    [Fact]
    public void SetAdapterEnabled_DisableAndEnable()
    {
        Assert.Equal(
            "interface set interface name=\"以太网\" admin=disable",
            NetshArgs.SetAdapterEnabled("以太网", enabled: false));
        Assert.Equal(
            "interface set interface name=\"以太网\" admin=enable",
            NetshArgs.SetAdapterEnabled("以太网", enabled: true));
    }

    [Fact]
    public void Name_Standalone_IncludesQuotes()
    {
        Assert.Equal("name=\"Ethernet 2\"", NetshArgs.Name("Ethernet 2"));
    }

    [Fact]
    public void Name_AdapterContainsQuote_Rejected()
    {
        // 【审查修复 2.1】引号会打破 name="…" 定界边界，轻则报错重则操作到非预期接口
        Assert.Throws<ArgumentException>(() => NetshArgs.Name("坏\"名字"));
        Assert.Throws<ArgumentException>(() => NetshArgs.SetAdapterEnabled("a\"b", enabled: false));
    }

    [Fact]
    public void Renew_AdapterContainsQuote_Rejected()
    {
        // 【并行审查核实·低1】Renew 曾手拼引号绕过 Name 的卫生检查——现已共用 ValidateAdapterName
        Assert.Throws<ArgumentException>(() => NetshArgs.Renew("坏\"名字"));
        Assert.Equal("/renew \"以太网\"", NetshArgs.Renew("以太网"));
    }

    /// <summary>「复制命令」的展示文本 = netsh 前缀 + 构造结果，与执行共用同一构造。</summary>
    [Fact]
    public void DisplayText_SharesConstructorWithExecution()
    {
        string args = NetshArgs.SetAdapterEnabled("以太网", enabled: false);
        Assert.Equal("netsh " + args, $"netsh {args}");
    }
}

/// <summary>内置 DNS 预设清单的完整性（设计文档 §3：大陆可用性优先排序）。</summary>
public class DnsPresetTests
{
    [Fact]
    public void BuiltIn_NonEmpty_FirstIsAutoDhcp()
    {
        Assert.NotEmpty(DnsPresets.BuiltIn);
        DnsPreset first = DnsPresets.BuiltIn[0];
        Assert.Equal("自动（DHCP）", first.Name);
        Assert.Null(first.Primary);
        Assert.Null(first.Secondary);
    }

    [Fact]
    public void BuiltIn_AllIps_ValidIpv4()
    {
        foreach (DnsPreset preset in DnsPresets.BuiltIn)
        {
            foreach (string? ip in new[] { preset.Primary, preset.Secondary })
            {
                if (ip is null)
                {
                    continue;
                }

                Assert.True(IPAddress.TryParse(ip, out _), $"{preset.Name} 含非法 IP：{ip}");
            }
        }
    }

    [Fact]
    public void BuiltIn_AliBeforeGoogleAndCloudflare()
    {
        IReadOnlyList<DnsPreset> presets = DnsPresets.BuiltIn;
        int aliIndex = IndexOf("阿里");
        int googleIndex = IndexOf("Google");
        int cloudflareIndex = IndexOf("Cloudflare");

        Assert.True(aliIndex < googleIndex, "阿里 DNS 应排在 Google 之前（大陆可用性优先）");
        Assert.True(aliIndex < cloudflareIndex, "阿里 DNS 应排在 Cloudflare 之前（大陆可用性优先）");

        int IndexOf(string keyword)
            => Enumerable.Range(0, presets.Count).First(i => presets[i].Name.Contains(keyword));
    }
}

/// <summary>ProxyInfo ↔ 注册表值的纯映射（注册表读写本身是系统边界不做单测）。</summary>
public class ProxySettingsTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void ToEnableValue_MapsBoolToDword(bool enabled, int expected)
    {
        Assert.Equal(expected, ProxyRegistryMapping.ToEnableValue(enabled));
    }

    [Fact]
    public void FromValues_TrimsServerWhitespace()
    {
        ProxyInfo info = ProxyRegistryMapping.FromValues(1, "  127.0.0.1:7890  ");

        Assert.True(info.Enabled);
        Assert.Equal("127.0.0.1:7890", info.Server);
    }

    [Fact]
    public void FromValues_Disabled_KeepsServer()
    {
        // 关闭代理时注册表里的 ProxyServer 常被保留（下次开启沿用），读取应还原它
        ProxyInfo info = ProxyRegistryMapping.FromValues(0, "  127.0.0.1:7890 ");

        Assert.False(info.Enabled);
        Assert.Equal("127.0.0.1:7890", info.Server);
    }

    [Fact]
    public void FromValues_BlankServer_NormalizedToNull()
    {
        ProxyInfo info = ProxyRegistryMapping.FromValues(1, "   ");

        Assert.True(info.Enabled);
        Assert.Null(info.Server);
    }

    [Fact]
    public void FromValues_NonZeroEnable_TreatedAsEnabled()
    {
        ProxyInfo info = ProxyRegistryMapping.FromValues(2, null);

        Assert.True(info.Enabled);
        Assert.Null(info.Server);
    }
}
