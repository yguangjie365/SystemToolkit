using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// hosts 解析与异常标记规则测试：注释/乱行容错、内网不标（控制误报）、
/// 高频域名指向非环回非内网才标「需留意」。自旧工程移植，L15 英文命名。
/// </summary>
public class HostsParserTests
{
    private const string Sample = """
		# Copyright 注释行
		127.0.0.1       localhost
		::1             localhost
		192.168.1.50    myserver.local myserver    # 内网自定义条目
		10.0.0.8        dev.internal
		93.184.216.34   www.baidu.com           # 高频域名指向公网 → 需留意
		1.2.3.4         login.qq.com taobao.com  # 高频域名指向公网 → 需留意
		这一行不是 IP 开头 应该被跳过
		192.168.1.9     # 只有 IP 没有域名，跳过
		""";

    [Fact]
    public void Parse_ToleratesCommentsAndGarbage_KeepsValidEntries()
    {
        IReadOnlyList<HostsEntry> entries = HostsParser.Parse(Sample);

        // localhost x2 + 内网 2 + 高频公网 2 = 6（「不是 IP」行与「只有 IP」行跳过）
        Assert.Equal(6, entries.Count);
    }

    [Fact]
    public void KnownDomain_PointingToPublicIp_MarkedSuspicious()
    {
        IReadOnlyList<HostsEntry> entries = HostsParser.Parse(Sample);

        var suspicious = entries.Where(e => e.Suspicious).ToList();
        Assert.Equal(2, suspicious.Count);
        Assert.Contains(suspicious, e => e.HostNames.Contains("www.baidu.com"));
        Assert.Contains(suspicious, e => e.HostNames.Contains("login.qq.com"));
    }

    [Fact]
    public void LoopbackAndPrivateEntries_NotMarked()
    {
        IReadOnlyList<HostsEntry> entries = HostsParser.Parse(Sample);

        Assert.DoesNotContain(entries.Where(e => e.Ip == "192.168.1.50"), e => e.Suspicious);
        Assert.DoesNotContain(entries.Where(e => e.Ip == "127.0.0.1"), e => e.Suspicious);
        Assert.DoesNotContain(entries.Where(e => e.Ip == "10.0.0.8"), e => e.Suspicious);
    }

    [Fact]
    public void KnownDomain_SubdomainForm_Matches()
    {
        HostsEntry entry = HostsParser.Parse("8.8.8.8 github.com").Single();
        Assert.True(entry.Suspicious);

        HostsEntry subDomain = HostsParser.Parse("8.8.8.8 gist.github.com").Single();
        Assert.True(subDomain.Suspicious); // 子域形式命中（EndsWith ".github.com"）

        HostsEntry unrelated = HostsParser.Parse("8.8.8.8 example.org").Single();
        Assert.False(unrelated.Suspicious); // 未命中清单
    }

    [Fact]
    public void EmptyText_ReturnsEmpty()
    {
        Assert.Empty(HostsParser.Parse(""));
    }
}
