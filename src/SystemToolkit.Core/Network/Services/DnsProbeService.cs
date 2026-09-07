using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace SystemToolkit.Core.Network.Services;

/// <summary>DNS 单次查询结果（Success=false 时 Error 说明原因：超时 / SERVREF / 报文异常）。</summary>
public sealed record DnsLookupResult(string Server, string Domain, bool Success, string? AnswerAddress, int? LatencyMs, string Error = "");

/// <summary>DNS 服务器基准统计（延迟单位 ms；全失败时三值为 null——「无从谈起」≠ 0）。</summary>
public sealed record DnsServerStats(string Server, int Queries, int Successes, double LossPercent, int? MinMs, int? AvgMs, int? MaxMs);

/// <summary>
/// DNS 定向查询与优选基准（2026-09-06 新增，设计 04 §2「DNS 查询检查」）。
/// <para>
/// 实现为 RFC 1035 标准查询报文的构造与解析（UDP :53，A 记录）——不经 nslookup.exe：
/// 其输出格式本地化且无结构，无法可靠解析（老工程规避 ping.exe 同一理由）。
/// 报文构造 / 应答解析为 internal 纯函数，可用 canned 字节单测，不发真实网络包。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DnsProbeService
{
    /// <summary>单次查询超时（毫秒）。</summary>
    public const int LookupTimeoutMs = 1500;

    /// <summary>优选基准的单服务器查询超时（毫秒）。</summary>
    public const int BenchmarkTimeoutMs = 1000;

    /// <summary>优选基准每服务器查询次数（评审对齐诊断量化 10 次口径）。</summary>
    public const int BenchmarkQueriesPerServer = 10;

    /// <summary>优选基准轮询的域名（大陆可达性优先，A 记录稳定）。</summary>
    public static readonly string[] BenchmarkDomains =
    [
        "www.baidu.com",
        "www.qq.com",
        "www.bilibili.com",
        "www.taobao.com",
    ];

    /// <summary>
    /// 向<b>指定 DNS 服务器</b>查询域名的 A 记录（系统解析器做不到这件事——它只认当前配置的 DNS）。
    /// </summary>
    public async Task<DnsLookupResult> LookupAsync(string server, string domain, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IpValidation.IsIPv4(server))
        {
            return new DnsLookupResult(server, domain, Success: false, null, null, "DNS 服务器地址非法（需 IPv4）");
        }

        byte[] query;
        try
        {
            query = BuildQuery((ushort)Random.Shared.Next(1, ushort.MaxValue), domain);
        }
        catch (ArgumentException ex)
        {
            return new DnsLookupResult(server, domain, Success: false, null, null, ex.Message);
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(IPAddress.Parse(server), port: 53);
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromMilliseconds(LookupTimeoutMs);
        var clock = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);
        try
        {
            await udp.SendAsync(query, timeoutCts.Token).ConfigureAwait(false);
            UdpReceiveResult response = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            clock.Stop();
            (string? address, string? error) = ParseAAnswer(response.Buffer);
            return address is not null
                ? new DnsLookupResult(server, domain, Success: true, address, (int)clock.ElapsedMilliseconds)
                : new DnsLookupResult(server, domain, Success: false, null, (int)clock.ElapsedMilliseconds, error ?? "应答中无 A 记录");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsLookupResult(server, domain, Success: false, null, null, $"查询超时（{(int)effectiveTimeout.TotalMilliseconds} ms 无应答）");
        }
        catch (SocketException ex)
        {
            return new DnsLookupResult(server, domain, Success: false, null, null, $"网络错误：{ex.Message}");
        }
    }

    /// <summary>
    /// DNS 优选基准：对每个服务器依序发 <see cref="BenchmarkQueriesPerServer"/> 次查询
    /// （域名从 <see cref="BenchmarkDomains"/> 轮询），统计成功率与延迟分布。服务器间串行、
    /// 进度逐查询上报，全程可取消。
    /// </summary>
    public async Task<IReadOnlyList<DnsServerStats>> BenchmarkAsync(
        IReadOnlyList<string> servers,
        IProgress<(string Server, int Done)>? progress = null,
        CancellationToken ct = default)
    {
        var stats = new List<DnsServerStats>(servers.Count);
        foreach (string server in servers)
        {
            var latencies = new List<int>(BenchmarkQueriesPerServer);
            int failures = 0;
            for (int i = 0; i < BenchmarkQueriesPerServer && !ct.IsCancellationRequested; i++)
            {
                string domain = BenchmarkDomains[i % BenchmarkDomains.Length];
                DnsLookupResult r = await LookupAsync(server, domain, TimeSpan.FromMilliseconds(BenchmarkTimeoutMs), ct).ConfigureAwait(false);
                if (r.Success && r.LatencyMs is int ms)
                {
                    latencies.Add(ms);
                }
                else
                {
                    failures++;
                }

                progress?.Report((server, i + 1));
            }

            stats.Add(BuildStats(server, BenchmarkQueriesPerServer, latencies, failures));
        }

        return stats;
    }

    /// <summary>统计聚合（internal 纯函数，便于边界用例单测）。</summary>
    internal static DnsServerStats BuildStats(string server, int queries, IReadOnlyList<int> latencies, int failures)
    {
        int? min = latencies.Count > 0 ? latencies.Min() : null;
        int? avg = latencies.Count > 0 ? (int)Math.Round(latencies.Average()) : null;
        int? max = latencies.Count > 0 ? latencies.Max() : null;
        return new DnsServerStats(
            server, queries, latencies.Count,
            Math.Round(failures * 100.0 / queries, 1),
            min, avg, max);
    }

    // ───────────────── 报文构造 / 解析（internal 纯函数） ─────────────────

    /// <summary>构造标准 A 记录查询报文：12 字节头 + QNAME + QTYPE=1(A) + QCLASS=1(IN)，RD=1。</summary>
    internal static byte[] BuildQuery(ushort id, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArgumentException("域名为空", nameof(domain));
        }

        string[] labels = domain.TrimEnd('.').Split('.');
        if (labels.Any(l => l.Length is 0 or > 63))
        {
            throw new ArgumentException($"域名标签非法（空或超 63 字符）：{domain}", nameof(domain));
        }

        int length = 12 + labels.Sum(l => l.Length + 1) + 5;
        byte[] buffer = new byte[length];
        int offset = 0;
        buffer[offset++] = (byte)(id >> 8);
        buffer[offset++] = (byte)(id & 0xFF);
        buffer[offset++] = 0x01; // flags 高位字节：RD=1
        buffer[offset++] = 0x00; // flags 低位字节
        buffer[offset++] = 0x00; // QDCOUNT 高位字节 = 1
        buffer[offset++] = 0x01;
        offset = 12; // ANCOUNT / NSCOUNT / ARCOUNT 六字节保持零初始化（数组默认清零，标签必须从 12 起写）
        foreach (string label in labels)
        {
            // ⚠️ 不能用「编码后找 0 字节」判非 ASCII：Encoding.ASCII 把非 ASCII 映射为 '?'(0x3F)
            if (label.Any(c => c > 127))
            {
                throw new ArgumentException($"域名含非 ASCII 字符，本实现仅支持 ASCII 域名：{domain}", nameof(domain));
            }

            buffer[offset++] = (byte)label.Length;
            byte[] ascii = System.Text.Encoding.ASCII.GetBytes(label);
            ascii.CopyTo(buffer, offset);
            offset += ascii.Length;
        }

        buffer[offset++] = 0x00; // QNAME 终止
        buffer[offset++] = 0x00; // QTYPE = 1 (A)
        buffer[offset++] = 0x01;
        buffer[offset++] = 0x00; // QCLASS = 1 (IN)
        buffer[offset] = 0x01;
        return buffer;
    }

    /// <summary>解析应答报文：取第一条 A 记录的 IPv4。返回 (地址, 错误)——成功时错误为 null。</summary>
    internal static (string? Address, string? Error) ParseAAnswer(byte[] buffer)
    {
        if (buffer.Length < 12)
        {
            return (null, "应答过短（非 DNS 报文）");
        }

        bool isResponse = (buffer[2] & 0x80) != 0;
        if (!isResponse)
        {
            return (null, "应答标志缺失（QR=0）");
        }

        byte rcode = (byte)(buffer[3] & 0x0F);
        if (rcode != 0)
        {
            return (null, RcodeText(rcode));
        }

        int answerCount = (buffer[6] << 8) | buffer[7];
        if (answerCount == 0)
        {
            return (null, "应答无记录（ANCOUNT=0）");
        }

        // 跳过问题区：QNAME（标签序列到 0 结束）+ QTYPE(2) + QCLASS(2)
        int offset = 12;
        while (offset < buffer.Length && buffer[offset] != 0)
        {
            offset += buffer[offset] + 1;
        }

        offset += 5;

        for (int i = 0; i < answerCount && offset + 10 <= buffer.Length; i++)
        {
            offset = SkipName(buffer, offset);
            if (offset + 10 > buffer.Length)
            {
                break;
            }

            int type = (buffer[offset] << 8) | buffer[offset + 1];
            int rdLength = (buffer[offset + 8] << 8) | buffer[offset + 9];
            offset += 10;
            if (type == 1 && rdLength == 4 && offset + 4 <= buffer.Length)
            {
                return ($"{buffer[offset]}.{buffer[offset + 1]}.{buffer[offset + 2]}.{buffer[offset + 3]}", null);
            }

            offset += rdLength;
        }

        return (null, null);
    }

    /// <summary>跳过资源记录的名称字段：压缩指针（0xC0 前缀）占 2 字节，标签序列按长度跳读。</summary>
    private static int SkipName(byte[] buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            int length = buffer[offset];
            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2; // 压缩指针
            }

            offset += length + 1;
        }

        return offset;
    }

    private static string RcodeText(byte rcode) => rcode switch
    {
        1 => "格式错误（FORMERR）",
        2 => "服务器故障（SERVFAIL）",
        3 => "域名不存在（NXDOMAIN）",
        4 => "未实现（NOTIMP）",
        5 => "拒绝应答（REFUSED）",
        _ => $"查询失败（RCODE={rcode}）",
    };
}
