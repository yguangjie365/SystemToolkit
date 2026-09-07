using System.Net;

namespace SystemToolkit.Core.Network.Services;

/// <summary>hosts 文件的单条非注释条目。</summary>
public sealed record HostsEntry(string Ip, IReadOnlyList<string> HostNames)
{
    /// <summary>【M6a 评审 Q4】是否需留意：命中高频域名清单且指向非环回、非内网地址
    /// （内网开发环境的自定义条目不标，控制误报）。</summary>
    public bool Suspicious => HostsRules.IsSuspicious(this);
}

/// <summary>
/// hosts 文件解析与异常标记（纯函数，M6b P1-4；<b>只呈现绝不修改</b>——写面太大不做）。
/// <para>
/// 解析容错：整行 <c>#</c> 注释跳过、行内 <c>#</c> 后截断、首 token 非 IP 的乱行跳过；
/// 每条目 = IP + 域名列表。
/// </para>
/// </summary>
public static class HostsParser
{
    /// <summary>内置高频域名清单：命中且指向非环回非内网才标「需留意」（评审 Q4：
    /// 不做用户可编辑白名单——维护成本＞收益）。</summary>
    public static readonly IReadOnlyList<string> KnownDomains = new[]
    {
        "baidu.com", "qq.com", "weixin.com", "wechat.com", "taobao.com", "tmall.com",
        "jd.com", "aliyun.com", "alipay.com", "bilibili.com", "zhihu.com", "douyin.com",
        "toutiao.com", "163.com", "126.com", "google.com", "youtube.com", "github.com",
        "microsoft.com", "windowsupdate.com", "apple.com", "adobe.com", "steamcontent.com",
        "steamcommunity.com",
    };

    /// <summary>解析 hosts 文本：整行注释跳过、行内 # 截断，首 token 非 IP 的乱行跳过，其余每行一条（IP + 域名列表）。</summary>
    public static IReadOnlyList<HostsEntry> Parse(string text)
    {
        var entries = new List<HostsEntry>();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            int hash = line.IndexOf('#');
            if (hash == 0)
            {
                continue; // 整行注释
            }

            if (hash > 0)
            {
                line = line[..hash].Trim(); // 行内注释截断
            }

            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || !IPAddress.TryParse(tokens[0], out _))
            {
                continue; // 乱行容错：首 token 不是 IP 的行一律跳过
            }

            entries.Add(new HostsEntry(tokens[0], tokens[1..]));
        }

        return entries;
    }
}

/// <summary>异常标记规则（与解析分离，便于规则表驱动测试）。</summary>
public static class HostsRules
{
    /// <summary>单条条目是否「需留意」：命中 <see cref="HostsParser.KnownDomains"/> 且指向非环回、非内网地址（<see cref="HostsEntry.Suspicious"/> 即由本规则驱动）。</summary>
    public static bool IsSuspicious(HostsEntry entry)
    {
        if (!IPAddress.TryParse(entry.Ip, out IPAddress? ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))  // IPv4 127.x / IPv6 ::1 都由静态 IsLoopback 覆盖
        {
            return false;
        }

        byte[] bytes = ip.GetAddressBytes();
        bool privateOrLocal = IPAddress.IsLoopback(ip)
            || (bytes.Length == 4 && (bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)));
        if (privateOrLocal)
        {
            return false;
        }

        return entry.HostNames.Any(h => HostsParser.KnownDomains.Any(d =>
            h.Equals(d, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)));
    }
}
