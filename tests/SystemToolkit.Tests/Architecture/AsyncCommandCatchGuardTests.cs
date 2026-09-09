using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 反模式 ⑤ 收口守卫（2026-09-10 全项目审查沉淀）：<c>[RelayCommand]</c> 命令体内必须
/// 就地 <c>catch (Exception)</c> 落用户可见处——AsyncRelayCommand/同步命令的异常都会被吞或直冲 UI 线程。
/// 实证：NetManager 4 个写命令 finally-only、Settings.Save 裸奔（2026-09-10 审查 🟠-2/3）。
/// 豁免：方法体任意位置带 <c>// guard-exempt: catch</c> 注释并说明理由（如调用方已统一兜底）。
/// </summary>
public class AsyncCommandCatchGuardTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("未找到仓库根");
    }

    private static readonly Regex RelayCommandAttr = new(@"\[RelayCommand[^\]]*\]\s*",
        RegexOptions.Compiled);

    // 只守 async Task——AsyncRelayCommand 的异常才会被吞；void 命令异常直冲 UI 线程
    // （可见、即时），且纯内存操作居多，纳入守卫会制造大量无意义 try/catch，改由人工审查。
    private static readonly Regex CommandSignature = new(
        @"private\s+async\s+Task\s+(\w+)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void RelayCommandBodies_MustContainCatch()
    {
        string modulesDir = Path.Combine(RepoRoot(), "src");

        foreach (string file in Directory.EnumerateFiles(modulesDir, "*.cs", SearchOption.AllDirectories))
        {
            // 只扫模块 VM 层（Shell/Infrastructure 的命令模型不同，不在本守卫范围）
            if (!file.Replace('\\', '/').Contains("/SystemToolkit.Modules.", StringComparison.Ordinal)
                || file.Contains("obj", StringComparison.Ordinal)
                || file.Contains("bin", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!RelayCommandAttr.IsMatch(lines[i]))
                {
                    continue;
                }

                // 特性之后找第一条命令签名（允许特性与签名之间隔注释/空行/特性）
                int sigLine = -1;
                Match sig = Match.Empty;
                for (int j = i + 1; j < Math.Min(i + 6, lines.Length); j++)
                {
                    sig = CommandSignature.Match(lines[j]);
                    if (sig.Success)
                    {
                        sigLine = j;
                        break;
                    }
                    if (lines[j].StartsWith("[", StringComparison.Ordinal) || lines[j].StartsWith("public", StringComparison.Ordinal)
                        || lines[j].StartsWith("private", StringComparison.Ordinal) && !CommandSignature.IsMatch(lines[j]))
                    {
                        break; // 已进入下个成员
                    }
                }

                if (sigLine < 0)
                {
                    continue;
                }

                // 提取方法体：从签名行的 { 起做花括号深度配对
                int bodyStart = lines[sigLine].IndexOf('{');
                int cursor = sigLine;
                while (bodyStart < 0 && cursor + 1 < lines.Length)
                {
                    cursor++;
                    bodyStart = lines[cursor].IndexOf('{');
                }
                if (bodyStart < 0)
                {
                    continue; // 表达式体成员（=>）：单表达式无法承载 try/catch，不在守卫范围
                }

                int depth = 0;
                int bodyEnd = lines.Length - 1;
                bool bodyEndFound = false;
                for (int j = cursor; j < lines.Length && !bodyEndFound; j++)
                {
                    int from = j == cursor ? bodyStart : 0;
                    for (int c = from; c < lines[j].Length; c++)
                    {
                        if (lines[j][c] == '{')
                        {
                            depth++;
                        }
                        else if (lines[j][c] == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                bodyEnd = j;
                                bodyEndFound = true;
                                break;
                            }
                        }
                    }
                }

                string body = string.Join("\n", lines[cursor..(bodyEnd + 1)]);
                string methodName = sig.Groups[1].Value; // 审查 O6：单组正则取 Groups[1]（此前 [2] 恒空 → 基线退化文件级、集合 Except 掩盖同文件新增）

                bool hasCatch = body.Contains("catch", StringComparison.Ordinal);
                bool exempt = body.Contains("guard-exempt: catch", StringComparison.Ordinal);
                if (!hasCatch && !exempt)
                {
                    current.Add($"{RelPath(file)}::{methodName}");
                }
            }
        }

        // 基线制（对照 UiTokenRatchet）：存量冻结只减不增；新增无 catch 命令即红
        string baselinePath = Path.Combine(RepoRoot(),
            "tests/SystemToolkit.Tests/Architecture/AsyncCommandCatchBaseline.json");
        if (!File.Exists(baselinePath))
        {
            // 首跑冻结初始化：当前命中即为存量（此后只减不增）
            File.WriteAllText(baselinePath,
                System.Text.Json.JsonSerializer.Serialize(current.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        string[] baseline = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(baselinePath)) ?? [];

        var newHits = current.Except(baseline).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var fixedHits = baseline.Except(current).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (fixedHits.Count > 0)
        {
            File.WriteAllText(baselinePath,
                System.Text.Json.JsonSerializer.Serialize(current.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        Assert.True(newHits.Count == 0,
            "新增 [RelayCommand] async 命令缺 catch 兜底（先修代码；确属已兜底场景加注释 " +
            "'// guard-exempt: catch <理由>'）：" + Environment.NewLine + string.Join(Environment.NewLine, newHits));
    }

    private readonly List<string> current = [];

    private static string RelPath(string absolute)
    {
        string root = RepoRoot();
        return absolute.StartsWith(root, StringComparison.Ordinal) ? absolute[(root.Length + 1)..] : absolute;
    }
}
