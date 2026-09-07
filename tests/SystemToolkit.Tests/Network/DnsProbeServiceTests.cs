using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// DNS 定向查询与优选基准测试（2026-09-06 新增服务）。报文构造 / 应答解析为
/// internal 纯函数——用 canned 字节钉死 RFC 1035 契约，不发真实网络包；
/// LookupAsync 只测输入校验分支（网络路径属系统边界）。
/// </summary>
public class DnsProbeServiceTests
{
    // ── 查询报文构造 ──

    [Fact]
    public void BuildQuery_KnownDomain_StructureMatchesRfc1035()
    {
        byte[] q = DnsProbeService.BuildQuery(0x1234, "www.example.com");

        // 头部：ID + RD=1 + QDCOUNT=1
        Assert.Equal(0x12, q[0]);
        Assert.Equal(0x34, q[1]);
        Assert.Equal(0x01, q[2]); // flags 高字节：RD=1
        Assert.Equal(0x00, q[3]);
        Assert.Equal(0x00, q[4]);
        Assert.Equal(0x01, q[5]); // QDCOUNT 低字节 = 1

        // QNAME：3www7example3com0
        byte[] expectedQname = [0x03, (byte)'w', (byte)'w', (byte)'w', 0x07,
            (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            0x03, (byte)'c', (byte)'o', (byte)'m', 0x00];
        Assert.Equal(expectedQname, q.Skip(12).Take(expectedQname.Length).ToArray());

        // 尾部：QTYPE=1(A) + QCLASS=1(IN)
        int tailStart = 12 + expectedQname.Length;
        // QTYPE=1（00 01）+ QCLASS=1（00 01）
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x01 }, q.Skip(tailStart).ToArray());
        Assert.Equal(tailStart + 4, q.Length);
    }

    [Fact]
    public void BuildQuery_TrailingDot_Trimmed()
    {
        byte[] withDot = DnsProbeService.BuildQuery(1, "example.com.");
        byte[] withoutDot = DnsProbeService.BuildQuery(1, "example.com");

        Assert.Equal(withoutDot, withDot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a..b")] // 空标签
    public void BuildQuery_InvalidDomains_ThrowArgumentException(string domain)
    {
        Assert.Throws<ArgumentException>(() => DnsProbeService.BuildQuery(1, domain));
    }

    [Fact]
    public void BuildQuery_LabelOver63Chars_Rejected()
    {
        string tooLong = new string('a', 64) + ".com";
        Assert.Throws<ArgumentException>(() => DnsProbeService.BuildQuery(1, tooLong));
    }

    [Fact]
    public void BuildQuery_NonAsciiDomain_Rejected()
    {
        Assert.Throws<ArgumentException>(() => DnsProbeService.BuildQuery(1, "中文.com"));
    }

    // ── 应答解析（canned 字节） ──

    /// <summary>构造标准应答：头部 + 问题区 + answers 正文（调用方拼资源记录）。</summary>
    private static byte[] Response(ushort id, byte rcode, int answerCount, bool isResponse = true)
    {
        var buffer = new List<byte>(12)
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            isResponse ? (byte)0x80 : (byte)0x00, rcode,
            0x00, 0x01, // QDCOUNT = 1
            (byte)(answerCount >> 8), (byte)(answerCount & 0xFF),
            0x00, 0x00, 0x00, 0x00, // NSCOUNT / ARCOUNT
        };
        // 问题区：example.com A IN
        buffer.AddRange([0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            0x03, (byte)'c', (byte)'o', (byte)'m', 0x00, 0x00, 0x01, 0x00, 0x01]);
        return [.. buffer];
    }

    private static void AddARecord(List<byte> buffer, byte a, byte b, byte c, byte d)
    {
        // NAME = 压缩指针指向问题区（偏移 12）；TYPE=1 CLASS=1 TTL=0 RDLENGTH=4
        buffer.Add(0xC0);
        buffer.Add(0x0C);
        buffer.AddRange([0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04]);
        buffer.AddRange([a, b, c, d]);
    }

    [Fact]
    public void ParseAAnswer_SingleARecord_ReturnsAddress()
    {
        List<byte> buffer = Response(0x1234, rcode: 0, answerCount: 1).ToList();
        AddARecord(buffer, 1, 2, 3, 4);

        (string? address, string? error) = DnsProbeService.ParseAAnswer([.. buffer]);

        Assert.Null(error);
        Assert.Equal("1.2.3.4", address);
    }

    [Fact]
    public void ParseAAnswer_CnameThenA_SkipsCnameFindsA()
    {
        List<byte> buffer = Response(0x1234, rcode: 0, answerCount: 2).ToList();
        // 第一条：CNAME（TYPE=5），RDATA = "www.example.com" 标签序列（3+1+7+1+3+1 = 17 字节）
        buffer.AddRange([0xC0, 0x0C, 0x00, 0x05, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x11]);
        buffer.AddRange([0x03, (byte)'w', (byte)'w', (byte)'w', 0x07, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 0x03, (byte)'c', (byte)'o', (byte)'m', 0x00]);
        // 第二条：A 记录
        AddARecord(buffer, 93, 184, 216, 34);

        (string? address, string? error) = DnsProbeService.ParseAAnswer([.. buffer]);

        Assert.Null(error);
        Assert.Equal("93.184.216.34", address);
    }

    [Theory]
    [InlineData(2, "SERVFAIL")]
    [InlineData(3, "NXDOMAIN")]
    [InlineData(5, "REFUSED")]
    public void ParseAAnswer_NonZeroRcode_ReturnsRcodeText(byte rcode, string expectedFragment)
    {
        byte[] buffer = Response(1, rcode, answerCount: 0);

        (string? address, string? error) = DnsProbeService.ParseAAnswer(buffer);

        Assert.Null(address);
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void ParseAAnswer_NotAResponse_QrBitMissing()
    {
        byte[] buffer = Response(1, rcode: 0, answerCount: 1, isResponse: false);

        (string? address, string? error) = DnsProbeService.ParseAAnswer(buffer);

        Assert.Null(address);
        Assert.Contains("QR=0", error);
    }

    [Fact]
    public void ParseAAnswer_NoAnswers_ReportsEmptyAnswer()
    {
        byte[] buffer = Response(1, rcode: 0, answerCount: 0);

        (string? address, string? error) = DnsProbeService.ParseAAnswer(buffer);

        Assert.Null(address);
        Assert.Contains("ANCOUNT=0", error);
    }

    [Fact]
    public void ParseAAnswer_TooShort_ReportsNotDns()
    {
        (string? address, string? error) = DnsProbeService.ParseAAnswer([0x00, 0x01, 0x02]);

        Assert.Null(address);
        Assert.Contains("应答过短", error);
    }

    // ── 统计聚合 ──

    [Fact]
    public void BuildStats_MixedResults_ComputesDistributionAndLoss()
    {
        DnsServerStats stats = DnsProbeService.BuildStats("223.5.5.5", queries: 10, [10, 20, 30], failures: 7);

        Assert.Equal(3, stats.Successes);
        Assert.Equal(70.0, stats.LossPercent);
        Assert.Equal(10, stats.MinMs);
        Assert.Equal(20, stats.AvgMs);
        Assert.Equal(30, stats.MaxMs);
    }

    [Fact]
    public void BuildStats_AllFailed_LatencyNullsNotZero()
    {
        // 「无从谈起」≠ 0：全失败时延迟三值为 null（与丢包量化同一纪律）
        DnsServerStats stats = DnsProbeService.BuildStats("223.5.5.5", queries: 10, [], failures: 10);

        Assert.Equal(0, stats.Successes);
        Assert.Equal(100.0, stats.LossPercent);
        Assert.Null(stats.MinMs);
        Assert.Null(stats.AvgMs);
        Assert.Null(stats.MaxMs);
    }

    // ── 输入校验（不发网络包） ──

    [Fact]
    public async Task LookupAsync_InvalidServer_ReturnsErrorWithoutNetwork()
    {
        var service = new DnsProbeService();

        DnsLookupResult result = await service.LookupAsync("not-an-ip", "example.com");

        Assert.False(result.Success);
        Assert.Contains("非法", result.Error);
        Assert.Null(result.AnswerAddress);
    }
}
