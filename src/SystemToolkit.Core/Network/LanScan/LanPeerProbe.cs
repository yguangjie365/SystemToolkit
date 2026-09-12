using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Network.LanScan;

/// <summary>邻居补充信息：ICMP TTL 与 NetBIOS 名（两者都拿不到时为全 null，UI 降级"—"）。</summary>
/// <param name="Ttl">ICMP 回包 TTL（无应答 null）。</param>
/// <param name="NetBiosName">nbtstat 反查计算机名（未启用 NetBIOS/被防火墙拦时 null）。</param>
public sealed record LanPeerInfo(int? Ttl, string? NetBiosName);

/// <summary>
/// TTL → OS 类别推断（纯函数）。🟡 定位是「推断」不是「识别」：TTL 可被路由跳数/ tun 改写，
/// 展示一律带「(推断)」后缀。真实版本号在免提权无凭据前提下拿不到（SMB2 匿名不返回 OS 字符串、
/// WMI 远程需管理员/凭据），V1 不做伪装。
/// </summary>
public static class LanOs
{
    /// <summary>初始 TTL 分类：128=Windows 系；64=Linux/Android/macOS 系；255=苹果旧设备/网络设备。
    /// 因链路衰减给容差区间（-7 跳）。</summary>
    public static string? Classify(int? ttl) => ttl switch
    {
        null => null,
        >= 121 and <= 128 => "Windows (推断)",
        >= 57 and <= 64 => "Linux / Android / macOS (推断)",
        >= 248 => "网络设备 / 老款苹果 (推断)",
        _ => null,
    };
}

/// <summary>nbtstat -A 输出解析（纯函数可测）。取回包计算机名：优先 &lt;20&gt;（工作站服务），否则首个 &lt;00&gt;。</summary>
public static class LanNbstat
{
    private static readonly Regex Entry = new(
        @"^\s+(\S+)\s+<(?<tag>[0-9A-Fa-f]{2})>\s+(?<kind>\S+)", RegexOptions.Compiled);

    /// <summary>解析 nbtstat 名称表提取计算机名；无 &lt;20&gt; 时回退首个 UNIQUE &lt;00&gt;，格式外一律 null。</summary>
    public static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? first00 = null;
        foreach (string line in output.Split('\n'))
        {
            Match m = Entry.Match(line);
            if (!m.Success)
            {
                continue;
            }

            string tag = m.Groups["tag"].Value.ToUpperInvariant();
            string name = m.Groups[1].Value;
            if (tag == "20")
            {
                return name;
            }

            if (tag == "00" && first00 is null && m.Groups["kind"].Value.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                first00 = name;
            }
        }

        return first00;
    }
}

/// <summary>
/// <see cref="ILanPeerProbe"/> 接缝：单 IP 的 TTL/NetBIOS 补充探测（假件可注入）。
/// </summary>
public interface ILanPeerProbe
{
    /// <summary>一次完整补充探测（内部自带超时与吞错，永不抛——失败返回空信息）。</summary>
    Task<LanPeerInfo> QueryAsync(string ipv4, CancellationToken ct = default);
}

/// <summary>
/// 真实现：ICMP TTL 走 BCL <see cref="Ping"/>（300ms）；计算机名走系统 <c>nbtstat -A</c>
/// （1.5s 超时杀进程）——刻意不自研 NBSTAT 报文（RFC1002 逆向细节多，系统自带工具即权威实现）。
/// 两者都免提权。
/// </summary>
public sealed class LanPeerProbe : ILanPeerProbe
{
    /// <summary>nbtstat 查询超时。</summary>
    public const int NbstatTimeoutMs = 1500;

    /// <summary>ICMP 单次超时。</summary>
    public const int PingTimeoutMs = 300;

    /// <inheritdoc/>
    public async Task<LanPeerInfo> QueryAsync(string ipv4, CancellationToken ct = default)
    {
        int? ttl = await ReadTtlAsync(ipv4, ct).ConfigureAwait(false);
        string? name = OperatingSystem.IsWindows()
            ? await QueryNetBiosNameAsync(ipv4, ct).ConfigureAwait(false)
            : null;
        return new LanPeerInfo(ttl, name);
    }

    /// <summary>单次 ping 判定文本（UI「Ping」行内反馈复用；永不抛）。</summary>
    public async Task<string> PingVerdictAsync(string ipv4, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(ipv4, PingTimeoutMs).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
            {
                int? ttl = reply.Options is null ? null : reply.Options.Ttl;
                return $"✅ {reply.RoundtripTime}ms" + (LanOs.Classify(ttl) is string os ? $" · TTL={ttl} {os}" : "");
            }

            return "❌ 超时无应答";
        }
        catch (Exception ex)
        {
            return "❌ " + ex.Message;
        }
    }

    private static async Task<int?> ReadTtlAsync(string ipv4, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(ipv4, PingTimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success && reply.Options is not null ? reply.Options.Ttl : null;
        }
        catch (Exception)
        {
            return null; // 无 ICMP 应答是常态（防火墙吞），静默降级
        }
        finally
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    private static async Task<string?> QueryNetBiosNameAsync(string ipv4, CancellationToken ct)
    {
        try
        {
            ProcessStartInfo psi = new("nbtstat", $"-A {ipv4}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NbstatTimeoutMs);
            try
            {
                await stdout.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    return null; // 已自然退出
                }

                return null;
            }

            return LanNbstat.Parse(await stdout.ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null; // nbtstat 缺失/输出异常一律降级
        }
    }
}
