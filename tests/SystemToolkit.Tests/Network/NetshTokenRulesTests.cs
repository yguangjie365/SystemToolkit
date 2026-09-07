using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 提权白名单「net 命令段」规则测试（2026-09-06 新增，随 ElevatingCommandRunner 沉淀）：
/// 每种合法写命令形状命中、白名单外（读命令 / 变形 / 注入尾缀 / 越界值）一律拒绝。
/// 与 <see cref="NetshArgs"/> 的输出逐字对位——规则漏一个形状 = 该操作在不提权时静默直连失败。
/// </summary>
public class NetshTokenRulesTests
{
    // ── netsh 命中形状 ──

    [Theory]
    [InlineData("interface ipv4 set address name=\"以太网\" source=dhcp")]
    [InlineData("interface ipv4 set dnsservers name=\"LAN 2\" source=dhcp")]
    [InlineData("interface set interface name=\"以太网\" admin=enable")]
    [InlineData("interface set interface name=\"以太网\" admin=disable")]
    [InlineData("winsock reset")]
    [InlineData("int ip reset")]
    [InlineData("interface tcp set global autotuninglevel=normal")]
    [InlineData("interface tcp set global autotuninglevel=experimental")]
    [InlineData("interface tcp set global rss=disabled")]
    [InlineData("interface tcp set global ecncapability=default")]
    public void IsElevatedWrite_NetshKnownWriteShapes_Match(string arguments)
    {
        Assert.True(NetshTokenRules.IsElevatedWrite("netsh", arguments), arguments);
    }

    [Theory]
    [InlineData("interface ipv4 set address name=\"以太网\" source=static address=192.168.1.10 mask=255.255.255.0 gateway=192.168.1.1")]
    [InlineData("interface ipv4 set address name=\"以太网\" source=static address=10.0.0.5 mask=255.0.0.0")]
    [InlineData("interface ipv4 set dnsservers name=\"以太网\" source=static address=223.5.5.5 register=primary")]
    [InlineData("interface ipv4 add dnsservers name=\"以太网\" address=223.6.6.6 index=2")]
    [InlineData("interface ipv4 set interface name=\"LAN\" metric=25")]
    public void IsElevatedWrite_NetshValuedWriteShapes_Match(string arguments)
    {
        Assert.True(NetshTokenRules.IsElevatedWrite("netsh", arguments), arguments);
    }

    // ── 二次校验：正则放行但值非法 → 拒绝 ──

    [Theory]
    [InlineData("interface ipv4 set address name=\"以太网\" source=static address=999.168.1.10 mask=255.255.255.0")]
    [InlineData("interface ipv4 set address name=\"以太网\" source=static address=192.168.1.10 mask=255.255.255.0 gateway=abc")]
    [InlineData("interface ipv4 set dnsservers name=\"以太网\" source=static address=1.2.3 register=primary")]
    [InlineData("interface ipv4 add dnsservers name=\"以太网\" address=not-an-ip index=2")]
    [InlineData("interface ipv4 set interface name=\"LAN\" metric=0")]     // 跃点下界 1
    [InlineData("interface ipv4 set interface name=\"LAN\" metric=10000")] // 跃点上界 9999
    public void IsElevatedWrite_ValueFailsSecondaryValidation_Rejected(string arguments)
    {
        Assert.False(NetshTokenRules.IsElevatedWrite("netsh", arguments), arguments);
    }

    // ── 白名单外：读命令 / 变形 / 尾缀注入 ──

    [Theory]
    [InlineData("interface tcp show global")]                          // 读命令
    [InlineData("interface ipv4 show interfaces")]                     // 读命令
    [InlineData("interface ipv4 set address name=\"以太网\" source=dhcp extra")] // 尾缀注入
    [InlineData("interface tcp set global autotuninglevel=foo")]       // 取值集外
    [InlineData("interface tcp set global rndis=enabled")]             // 未登记设置项
    [InlineData("")]                                                   // 空参数
    public void IsElevatedWrite_NetshOutsideWhitelist_Rejected(string arguments)
    {
        Assert.False(NetshTokenRules.IsElevatedWrite("netsh", arguments), arguments);
    }

    // ── ipconfig / arp ──

    [Theory]
    [InlineData("ipconfig", "/flushdns", true)]
    [InlineData("ipconfig", "/renew \"以太网\"", true)]
    [InlineData("ipconfig", "/all", false)]
    [InlineData("ipconfig", "/release", false)]
    [InlineData("arp", "-d *", true)]
    [InlineData("arp", "-a", false)]
    [InlineData("reg", "add HKLM\\Software /v x", false)]  // 白名单外程序一律拒绝
    public void IsElevatedWrite_IpconfigArpAndUnknownPrograms(string fileName, string arguments, bool expected)
    {
        Assert.Equal(expected, NetshTokenRules.IsElevatedWrite(fileName, arguments));
    }

    // ── 归一化：全路径 + .exe 后缀照样识别 ──

    [Fact]
    public void IsElevatedWrite_FullPathWithExeSuffix_Normalized()
    {
        Assert.True(NetshTokenRules.IsElevatedWrite(
            @"C:\Windows\System32\netsh.exe", "winsock reset"));
        Assert.True(NetshTokenRules.IsElevatedWrite(
            @"C:\Windows\System32\ipconfig.EXE", "/flushdns"));
    }
}
