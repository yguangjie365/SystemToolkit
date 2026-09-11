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
                bool hasTopCatch = false; // v6 O-1c：只认**方法体顶层**（深度=1）的 catch
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
                        else if (BodyHasTopLevelCatch(lines[j], c, depth))
                        {
                            // 嵌套 lambda / 局部函数内的 catch（深度>1）不算数——
                            // v6 O-1c 实证：FTDVM.StopTransferAsync 的嵌套兜底曾骗过旧判据，
                            // 让守卫把无兜底命令从基线自动摘除（棘轮假性收紧）
                            hasTopCatch = true;
                        }
                    }
                }

                string body = string.Join("\n", lines[cursor..(bodyEnd + 1)]);
                string methodName = sig.Groups[1].Value; // 审查 O6：单组正则取 Groups[1]（此前 [2] 恒空 → 基线退化文件级、集合 Except 掩盖同文件新增）

                bool hasCatch = hasTopCatch;
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

        // 审查 v7（A-2）：**先断言新增为零，再考虑回写**。旧实现"fixedHits>0 即用 current 覆盖基线"
        // 会在混合轮次（既修复若干、又新增若干）时把新增违规自动吸收进基线——
        // v6 判据收紧当轮，RestoreEnvironmentAsync/BatchInstallAsync 两条就被这样无声入库。
        // 回写只允许发生在"纯修复轮"（MayRewriteBaseline 反向验证见下方用例）。
        Assert.True(newHits.Count == 0,
            "新增 [RelayCommand] async 命令缺 catch 兜底（先修代码；确属已兜底场景加注释 " +
            "'// guard-exempt: catch <理由>'）：" + Environment.NewLine + string.Join(Environment.NewLine, newHits));

        if (MayRewriteBaseline(newHits.Count) && fixedHits.Count > 0)
        {
            File.WriteAllText(baselinePath,
                System.Text.Json.JsonSerializer.Serialize(current.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private readonly List<string> current = [];

    private static string RelPath(string absolute)
    {
        string root = RepoRoot();
        return absolute.StartsWith(root, StringComparison.Ordinal) ? absolute[(root.Length + 1)..] : absolute;
    }

    /// <summary>v6 O-1c 判据助手：该字符位置是否为**方法体顶层**（深度=1）的 catch 关键字起点。
    /// 抽成方法以便反向验证（规则 6：注入违规 → 红 → 恢复 → 绿）。</summary>
    internal static bool BodyHasTopLevelCatch(string line, int charIndex, int depth)
        => depth == 1
            && charIndex + 5 <= line.Length
            && line.IndexOf("catch", charIndex, StringComparison.Ordinal) == charIndex;

    // ── 反向验证（v6 O-1c）：判据必须能区分"顶层 catch"与"仅嵌套 catch"──

    [Fact]
    public void Judge_TopLevelCatch_Counts()
    {
        // 模拟方法体：try 在深度 1，catch 也在深度 1
        Assert.True(BodyHasTopLevelCatch("        catch (Exception ex)", 8, depth: 1));
    }

    [Fact]
    public void Judge_NestedLambdaCatch_DoesNotCount()
    {
        // 嵌套 lambda 内的 catch（深度=3）：不算命令体兜底——这正是 v5 引入的骗过路径
        Assert.False(BodyHasTopLevelCatch("                    catch (Exception ex)", 20, depth: 3));
    }

    [Fact]
    public void Judge_NoCatch_ReturnsFalse()
    {
        Assert.False(BodyHasTopLevelCatch("            await Task.Delay(1);", 12, depth: 1));
    }

    // ── 反向验证（v7 A-2）：基线回写只允许发生在"纯修复轮"（无新增命中）──

    [Fact]
    public void Rewrite_PureFixRound_Allowed()
    {
        Assert.True(MayRewriteBaseline(newHitsCount: 0));
    }

    [Fact]
    public void Rewrite_MixedRound_WithNewHits_Forbidden()
    {
        // 混合轮（既修复又新增）：回写会把新增违规吸收进基线——v6 实证的两条无声入库即此路径
        Assert.False(MayRewriteBaseline(newHitsCount: 2));
    }

    /// <summary>v7 A-2 判据助手：仅当无新增命中时才允许用 current 覆盖基线。</summary>
    internal static bool MayRewriteBaseline(int newHitsCount) => newHitsCount == 0;
}
