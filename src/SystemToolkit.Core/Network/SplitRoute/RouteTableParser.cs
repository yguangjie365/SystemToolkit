using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Network.SplitRoute;

/// <summary>路由表一行（<c>netsh interface ipv4 show route</c> 视角）。接口名不在该表列内，
/// 匹配一律用 <see cref="IfIndex"/> + 网关（InterfaceTableParser 同款 locale 无关纪律）。</summary>
/// <param name="IfIndex">接口索引。</param>
/// <param name="Prefix">目标网段 CIDR。</param>
/// <param name="Nexthop">下一跳网关。</param>
public sealed record RouteRow(int IfIndex, string Prefix, string Nexthop)
{
    /// <summary>是否默认路由（0.0.0.0/0）。</summary>
    public bool IsDefault => SplitCidr.IsDefault(Prefix);
}

/// <summary>
/// IPv4 路由表解析器（纯函数）。行形态（真机以 ① 无 MTU 列 ② 有 MTU 列 ③ 尾随 publish/On-link
/// 本地化列三种变体做容错）：<c>*  11  75  0.0.0.0/0  192.168.1.1  No</c>。
/// 判据：可选 <c>*</c> → 接口号 →（可选 MTU）→ <c>ip/len</c> → <c>ip</c>，后续列忽略——
/// 表头/分隔线/中文列名天然不匹配，locale 无关（InterfaceTableParser 同范式）。
/// </summary>
public static partial class RouteTableParser
{
    private const string Ip = @"\d{1,3}(?:\.\d{1,3}){3}";

    [GeneratedRegex(@"^\s*\*?\s+(\d+)(?:\s+(\d+))?\s+(" + Ip + @")/(\d{1,2})\s+(" + Ip + @")(?=\s|$)")]
    private static partial Regex RowRegex();

    /// <summary><c>netsh interface ipv4 show interfaces</c> 行：接口索引 + 名称 + 当前跃点。</summary>
    public sealed record InterfaceRow(int IfIndex, string Name, int Metric);

    /// <summary>接口表解析（真机样本形态同 InterfaceTableParser：Idx Met MTU 状态 名称，无 Type 列）。</summary>
    public static IReadOnlyList<InterfaceRow> ParseInterfaces(IEnumerable<string> lines)
    {
        List<InterfaceRow> rows = [];
        foreach (string raw in lines)
        {
            Match m = IfaceRegex().Match(raw.TrimEnd());
            if (!m.Success
                || !int.TryParse(m.Groups[1].Value, out int idx)
                || !int.TryParse(m.Groups[2].Value, out int metric))
            {
                continue;
            }

            string name = m.Groups[3].Value.Trim();
            if (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(new InterfaceRow(idx, name, metric));
        }

        return rows;
    }

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+\d+\s+\S+\s+(.+\S)\s*$")]
    private static partial Regex IfaceRegex();

    /// <summary>逐行解析：locale 无关数字 token 驱动；表头/分隔线/越界前缀静默跳过。</summary>
    public static IReadOnlyList<RouteRow> Parse(IEnumerable<string> lines)
    {
        List<RouteRow> rows = [];
        foreach (string raw in lines)
        {
            Match m = RowRegex().Match(raw.TrimEnd());
            if (!m.Success
                || !int.TryParse(m.Groups[1].Value, out int idx)
                || !int.TryParse(m.Groups[4].Value, out int len) || len is < 0 or > 32)
            {
                continue; // 表头 / 分隔线 / 越界前缀
            }

            rows.Add(new RouteRow(idx, $"{m.Groups[3].Value}/{len}", m.Groups[5].Value));
        }

        return rows;
    }
}
