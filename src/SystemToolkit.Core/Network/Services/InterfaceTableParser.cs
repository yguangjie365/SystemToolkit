using System.Text.RegularExpressions;
using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <c>netsh interface ipv4 show interfaces</c> 表格解析器（纯函数，M6c P1-6）。
/// <para>
/// 行形态（真机 Win11 24H2 实测）：<c>Idx  Met  MTU  状态  名称</c>——状态列随系统
/// 语言可能为 connected / 已连接，解析不依赖状态列语言：前三个 token 必须是数字，
/// 名称 = 第四个 token 起的全部剩余（名称可含空格，如「本地连接* 3」）。
/// 表头 / 分隔线 / Loopback 伪接口（无路由意义）跳过。
/// </para>
/// </summary>
public static partial class InterfaceTableParser
{
    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(\d+)\s+\S+\s+(.+\S)\s*$")]
    private static partial Regex RowRegex();

    /// <summary>逐行解析：前三个 token 均为数字的数据行 →（接口名 + 当前跃点数，名称 = 第四 token 起剩余、可含空格）；表头 / 分隔线 / Loopback 伪接口跳过。</summary>
    public static IReadOnlyList<InterfaceMetricInfo> Parse(IEnumerable<string> lines)
    {
        var result = new List<InterfaceMetricInfo>();
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            Match m = RowRegex().Match(line);
            if (!m.Success)
            {
                continue; // 表头 / 分隔线 / 空行
            }

            string name = m.Groups[4].Value.Trim();
            if (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 伪接口无路由意义
            }

            result.Add(new InterfaceMetricInfo(name, int.Parse(m.Groups[2].Value)));
        }

        return result;
    }
}
