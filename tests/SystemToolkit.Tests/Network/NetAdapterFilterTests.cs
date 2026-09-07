using System.Net;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Tests;

/// <summary>
/// 幽灵网卡判定（2026-09-07 实机实证：Killer AX1690i 机型驱动升级残留接口
/// 「WLAN 2 / WLAN 4 / WLAN 5」混入 NetManager 网卡列表——设备管理器仅 1 块物理卡）。
/// 判据见 NetAdapterFilter.IsGhostAdapter 注释；用例数据取自主人实机截图。
/// </summary>
public class NetAdapterFilterTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void Up_Adapter_IsNeverGhost_EvenWithoutIpv4()
    {
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: true, speedBitsPerSecond: -1, [], connectionName: "WLAN"));
    }

    [Fact]
    public void Down_KnownSpeed_NotGhost_RealLanCase_1Gbps()
    {
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: 1_000_000_000, [], connectionName: "以太网"));
    }

    [Fact]
    public void Down_UnknownSpeed_ApiPaOnly_IsGhost_RealWlan2Case()
    {
        Assert.True(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: -1, [Ip("169.254.141.239")], connectionName: "WLAN 2"));
    }

    [Fact]
    public void Down_UnknownSpeed_NoIpv4_IsGhost_RealWlan4Case()
    {
        Assert.True(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: 0, [], connectionName: "WLAN 4"));
    }

    [Fact]
    public void Down_UnknownSpeed_RealStaticIpv4_NotGhost()
    {
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: -1, [Ip("192.168.1.50")], connectionName: "以太网"));
    }

    [Fact]
    public void Down_UnknownSpeed_ApiPaMixedWithRealIp_NotGhost()
    {
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: -1, [Ip("169.254.21.210"), Ip("10.0.0.7")], connectionName: "WLAN 5"));
    }

    [Fact]
    public void Ipv6Address_DoesNotCountAsRealIpv4Evidence()
    {
        // GetAddressBytes 长度非 4 的一律不构成"真实 IPv4"证据
        Assert.True(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: -1, [IPAddress.IPv6Loopback], connectionName: "WLAN 2"));
    }

    [Fact]
    public void NoMacAddress_IsGhost_EvenWhenUpWithRealIp()
    {
        // 无 MAC = 软件抽象层：即便"已连接 + 有真实 IP"也不算物理网卡
        Assert.True(NetAdapterFilter.IsGhostAdapter(
            isUp: true, speedBitsPerSecond: 1_000_000_000, [Ip("192.168.1.3")],
            connectionName: "WLAN", macAddressBytes: 0));
    }

    [Fact]
    public void ValidMac_WithRealIp_NotGhost()
    {
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: -1, [Ip("192.168.1.50")], connectionName: "以太网"));
    }

    // ===== 逃生舱：手动禁用的真实物理卡（外部评审 2026-09-07） =====

    [Fact]
    public void ManuallyDisabledPhysicalAdapter_CleanName_IsRetained()
    {
        // 禁用后：断开 + 速率未知 + 无 IPv4，与幽灵同貌；但连接名干净 → 必须保留，
        // 否则用户在 NetManager 中无法重新启用它
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: -1, [], connectionName: "WLAN"));
    }

    [Fact]
    public void ManuallyDisabledPhysicalAdapter_ChineseName_IsRetained()
    {
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: 0, [], connectionName: "以太网"));
    }

    [Fact]
    public void NumberedSuffix_WithoutTrailingDigits_IsNotAutoNumber()
    {
        // "WLAN 2 Pro" 以字母结尾，不是编号后缀 → 按真实卡保留
        Assert.False(NetAdapterFilter.IsGhostAdapter(
            isUp: false, speedBitsPerSecond: -1, [], connectionName: "WLAN 2 Pro"));
    }

    // ===== IsJunkAdapter：Span 化后语义回归（大小写不敏感 + 描述/名称两侧） =====

    [Theory]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter", true)]
    [InlineData("Killer Wi-Fi Direct Virtual Adapter #2", true)]
    [InlineData("Hyper-V Virtual Switch", true)]
    [InlineData("Killer Wi-Fi 6E AX1690i 160MHz Wireless Network Adapter (211NGW)", false)]
    public void IsJunkAdapter_MatchesBlacklistCaseInsensitively(string description, bool expected)
        => Assert.Equal(expected, NetAdapterFilter.IsJunkAdapter(description));

    [Fact]
    public void IsJunkAdapter_AlsoInspectsConnectionName()
    {
        // 垃圾特征可能只出现在连接名里（如"本地连接* 3"）
        Assert.True(NetAdapterFilter.IsJunkAdapter("Realtek PCIe GbE Family Controller", "本地连接* 3"));
    }
}
