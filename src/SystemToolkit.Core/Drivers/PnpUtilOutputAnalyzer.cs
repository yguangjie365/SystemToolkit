using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// pnputil / ElevatedHelper 输出的语言无关分析器。
/// 经验来源：DriverStoreExplorer PNPUtil.cs——pnputil 退出码与本地化文本都不可全信，
/// 成败判定需基于数字计数比对（Add）与结构化标记行（ElevatedHelper 分段协议）。
/// </summary>
public static class PnpUtilOutputAnalyzer
{
    // 匹配行尾计数（兼容 ASCII ':' 与全角 '：'、任意多空格）：
    // 中文系统："驱动程序包总数:      1"；英文系统："Total driver packages: 1"
    private static readonly Regex CountLineRegex = new(@"[:：]\s*(\d+)\s*$", RegexOptions.Compiled);

    // ElevatedHelper 分段标记行："[exit 0] pnputil /delete-driver oem1.inf D:\x"（Program.cs 硬编码格式）
    private static readonly Regex SegmentExitRegex = new(@"^\[exit (-?\d+)\] (.+)$", RegexOptions.Compiled);

    /// <summary>
    /// 解析 pnputil /add-driver 输出末尾的「总数 / 已添加数」两个计数。
    /// 返回 null = 解析不出（输出格式变化），调用方应退回退出码判定并显式注明。
    /// 判定规则（RAPR AddResultRegex 思路）：added == 0 或 added != total → 失败。
    /// </summary>
    public static (int Total, int Added)? ParseAddDriverCounts(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var numbers = new List<int>();
        foreach (string line in output.Split('\n'))
        {
            Match m = CountLineRegex.Match(line.TrimEnd('\r'));
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
            {
                numbers.Add(n);
            }
        }

        // 取输出末尾的两个计数（总数、已添加数）——前面的匹配行可能是枚举残留
        if (numbers.Count < 2)
        {
            return null;
        }

        return (numbers[^2], numbers[^1]);
    }

    /// <summary>
    /// 从 ElevatedHelper 分段输出中提取失败的子命令明细（exit != 0 的段）。
    /// 空列表 = 所有段都成功。
    /// </summary>
    public static List<(int ExitCode, string Description)> ExtractFailedSegments(string helperOutput)
    {
        var failed = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(helperOutput))
        {
            return failed;
        }

        foreach (string line in helperOutput.Split('\n'))
        {
            Match m = SegmentExitRegex.Match(line.TrimEnd('\r').TrimStart('\uFEFF'));
            if (m.Success && int.TryParse(m.Groups[1].Value, out int exit) && exit != 0)
            {
                failed.Add((exit, m.Groups[2].Value.Trim()));
            }
        }

        return failed;
    }

    /// <summary>
    /// Add 结果双重判定（RAPR AddResultRegex 经验）：退出码为 0 后再比对输出末尾「总数/已添加数」——
    /// pnputil 对"添加 0 个包"退出码也可能是 0，仅凭退出码会误报成功。
    /// 计数解析不出（未来格式变化）→ 保守按成功返回并显式注明；added==0 或 added≠total → 判失败。
    /// </summary>
    public static DriverRunResult VerifyAddResult(DriverRunResult result)
    {
        if (!result.Success)
        {
            return result;
        }

        (int Total, int Added)? counts = ParseAddDriverCounts(result.Output);
        if (counts is null)
        {
            return result with { Output = result.Output + "\n[警告] 未能从输出解析添加计数，本次成功判定仅基于退出码" };
        }

        if (counts.Value.Added == 0 || counts.Value.Added != counts.Value.Total)
        {
            return new DriverRunResult(
                false,
                result.ExitCode,
                $"{result.Output}\n[校验失败] 计数比对：总数 {counts.Value.Total} ≠ 已添加 {counts.Value.Added}（INF 可能无效、签名被拒或已存在）");
        }

        return result;
    }
}
