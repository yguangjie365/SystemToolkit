using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 提权通道「net 命令段」白名单（双端同源：Core 端 <c>ElevatingCommandRunner</c> 判定、
/// ElevatedHelper 端执行前复核——规则单点本类，先例 <c>PnpUtilTokenRules</c>）。
/// <para>
/// Elevated Helper 以管理员运行，不信任调用方拼好的参数：任何一段不命中白名单即整批拒绝（零执行）。
/// 形状以 <see cref="NetshArgs"/> 与 <c>NetRepairService</c> 的实际构造逐条核对（真机 Win11 netsh
/// <c>set global</c> 帮助输出核实 autotuninglevel/rss/ecncapability 取值集）；IP 值在正则命中后
/// 再经 <see cref="IpValidation.IsIPv4"/> 严格校验（正则无法表达 ≤255）。
/// </para>
/// </summary>
public static partial class NetshTokenRules
{
    private const string IpPattern = @"\d{1,3}(?:\.\d{1,3}){3}";

    // ── netsh 写命令形状（与 NetshArgs 输出逐字对应；name="…" 内嵌引号由正则原样匹配）──

    [GeneratedRegex(@"^interface ipv4 set address name=""[^""]+"" source=dhcp$")]
    private static partial Regex SetDhcp();

    [GeneratedRegex(@"^interface ipv4 set address name=""[^""]+"" source=static address=(" + IpPattern + @") mask=(" + IpPattern + @")( gateway=(" + IpPattern + @"))?$")]
    private static partial Regex SetStaticIp();

    [GeneratedRegex(@"^interface ipv4 set dnsservers name=""[^""]+"" source=dhcp$")]
    private static partial Regex SetDnsToDhcp();

    [GeneratedRegex(@"^interface ipv4 set dnsservers name=""[^""]+"" source=static address=(" + IpPattern + @") register=primary$")]
    private static partial Regex SetDnsPrimary();

    [GeneratedRegex(@"^interface ipv4 add dnsservers name=""[^""]+"" address=(" + IpPattern + @") index=2$")]
    private static partial Regex AddDnsSecondary();

    [GeneratedRegex(@"^interface ipv4 set interface name=""[^""]+"" metric=(\d{1,4})$")]
    private static partial Regex SetInterfaceMetric();

    [GeneratedRegex(@"^interface set interface name=""[^""]+"" admin=(enable|disable)$")]
    private static partial Regex SetAdapterEnabled();

    // 单次调用只发一个设置项（TcpTuningService 差量应用逐项构造；取值集=真机 netsh 帮助输出）
    [GeneratedRegex(@"^interface tcp set global (autotuninglevel=(disabled|highlyrestricted|restricted|normal|experimental)|rss=(disabled|enabled|default)|ecncapability=(disabled|enabled|default))$")]
    private static partial Regex TcpSetGlobal();

    // NET-7 分流：route 增删（netsh 官方顺序 prefix → interface → nexthop；值域二次严格校验见 MatchNetsh）
    [GeneratedRegex(@"^interface ipv4 add route prefix=(" + IpPattern + @")/(\d{1,2}) interface=""[^""]+"" nexthop=(" + IpPattern + @") store=(persistent|active)( metric=(\d{1,4}))?$")]
    private static partial Regex AddRoute();

    [GeneratedRegex(@"^interface ipv4 delete route prefix=(" + IpPattern + @")/(\d{1,2}) interface=""[^""]+"" nexthop=(" + IpPattern + @")$")]
    private static partial Regex DeleteRoute();

    [GeneratedRegex(@"^winsock reset$")]
    private static partial Regex WinsockReset();

    [GeneratedRegex(@"^int ip reset$")]
    private static partial Regex IpReset();

    // ── ipconfig / arp 写命令形状（NetRepairService 构造）──

    [GeneratedRegex(@"^/flushdns$")]
    private static partial Regex IpconfigFlushDns();

    [GeneratedRegex(@"^/renew ""[^""]+""$")]
    private static partial Regex IpconfigRenew();

    [GeneratedRegex(@"^-d \*$")]
    private static partial Regex ArpDeleteAll();

    /// <summary>判定 (fileName, arguments) 是否为白名单内的提权写命令。白名单外一律 false（不提权、不执行）。</summary>
    public static bool IsElevatedWrite(string fileName, string arguments)
    {
        switch (Normalize(fileName))
        {
            case "netsh":
                return MatchNetsh(arguments);
            case "ipconfig":
                return IpconfigFlushDns().IsMatch(arguments) || IpconfigRenew().IsMatch(arguments);
            case "arp":
                return ArpDeleteAll().IsMatch(arguments);
            default:
                return false;
        }
    }

    private static bool MatchNetsh(string arguments)
    {
        if (SetDhcp().IsMatch(arguments)
            || SetDnsToDhcp().IsMatch(arguments)
            || SetAdapterEnabled().IsMatch(arguments)
            || WinsockReset().IsMatch(arguments)
            || IpReset().IsMatch(arguments)
            || TcpSetGlobal().IsMatch(arguments))
        {
            return true;
        }

        // 带值形状：正则命中后对 IP / 跃点数值做二次严格校验
        Match m = SetStaticIp().Match(arguments);
        if (m.Success)
        {
            var ips = new List<string> { m.Groups[1].Value, m.Groups[2].Value };
            if (m.Groups[4].Success)
            {
                ips.Add(m.Groups[4].Value);
            }

            return ips.TrueForAll(IpValidation.IsIPv4);
        }

        m = SetDnsPrimary().Match(arguments);
        if (m.Success)
        {
            return IpValidation.IsIPv4(m.Groups[1].Value);
        }

        m = AddDnsSecondary().Match(arguments);
        if (m.Success)
        {
            return IpValidation.IsIPv4(m.Groups[1].Value);
        }

        m = SetInterfaceMetric().Match(arguments);
        if (m.Success)
        {
            return int.TryParse(m.Groups[1].Value, out int metric) && metric is >= 1 and <= 9999;
        }

        m = AddRoute().Match(arguments);
        if (m.Success)
        {
            // 组序：1=目标IP 2=前缀长度 3=nexthop 4=store 5/6=metric（metric 数字组=6）
            return ValidRoute(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
                && (!m.Groups[6].Success || int.TryParse(m.Groups[6].Value, out int rm) && rm is >= 1 and <= 9999);
        }

        m = DeleteRoute().Match(arguments);
        if (m.Success)
        {
            return ValidRoute(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        }

        return false;
    }

    /// <summary>route 三值联合校验：目标 IP / 前缀长度 0..32 / 网关 IP 全严格才放行。</summary>
    private static bool ValidRoute(string prefixIp, string prefixLen, string nexthop) =>
        IpValidation.IsIPv4(prefixIp)
        && int.TryParse(prefixLen, out int len) && len is >= 0 and <= 32
        && IpValidation.IsIPv4(nexthop);

    /// <summary>归一化可执行名："C:\Windows\System32\netsh.exe" → "netsh"。无法识别返回空串。</summary>
    private static string Normalize(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "";
        }

        string name = fileName;
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        return name.ToLowerInvariant();
    }
}
