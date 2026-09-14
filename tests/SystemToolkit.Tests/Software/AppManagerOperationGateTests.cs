using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 「winget 写操作闸门」兜底覆盖锁（V11-A2/A3，2026-09-14）。
/// <para>
/// 缺陷类：<c>AcquireOperationAsync()</c> 占用 <c>_wingetGate</c> 并置 <c>IsOperating = true</c>，
/// 而**释放只发生在命令体后续 try/finally 里** ⇒ 只要进闸本身抛/取消、或进闸与进入 try 之间
/// 存在可抛语句，闸门与忙态就**永久不释放**（此后安装/升级/卸载恒被拒，且零日志零反馈）。
/// </para>
/// <para>
/// 🔴 为什么既有 <c>AsyncCommandCatchGuardTests</c> 抓不到：其判据是「方法体内**是否存在**顶层
/// catch」，**不判「是否覆盖全部可抛语句」** ⇒「catch 在、进闸语句裸奔」长期存活且全绿
/// （v12 核实记录已把此语义盲区登记进 P4 守卫覆盖体检）。本锁以**结构不变量**补位：
/// 凡调用 <c>AcquireOperationAsync()</c> 的命令体，进闸点之后必须出现深度=1 的 catch。
/// </para>
/// </summary>
public class AppManagerOperationGateTests
{
    /// <summary>进闸调用（唯一语义：占用 _wingetGate + 置忙）。</summary>
    private static readonly Regex AcquireCall = new(@"await\s+AcquireOperationAsync\s*\(\s*\)\s*;", RegexOptions.Compiled);

    private static readonly Regex CommandSignature = new(@"private\s+async\s+Task\s+(\w+)\s*\(", RegexOptions.Compiled);

    /// <summary>期望的进闸点清单——改动此表即等于改动契约，必须同步审视兜底位置。</summary>
    private static readonly (string File, string Method)[] ExpectedAcquireCallers =
    [
        ("AppManagerViewModel.Refresh.cs", "InstallAsync"),
        ("AppManagerViewModel.Refresh.cs", "UpgradeAsync"),
        ("AppManagerViewModel.Refresh.cs", "UninstallAsync"),
        ("AppManagerViewModel.Search.cs", "InstallSearchResultAsync"),
        ("AppManagerViewModel.Manual.cs", "ExportInstalledAsync"),
    ];

    [Fact]
    public void EveryAcquirePoint_MustBeFollowedByTopLevelCatch()
    {
        var violations = new List<string>();
        var found = new List<string>();

        foreach ((string file, string method) in ExpectedAcquireCallers)
        {
            string[] lines = File.ReadAllLines(Path.Combine(ModuleDir(), file));
            bool checkedThisOne = false;

            for (int i = 0; i < lines.Length; i++)
            {
                Match sig = CommandSignature.Match(lines[i]);
                if (!sig.Success || sig.Groups[1].Value != method)
                {
                    continue;
                }

                checkedThisOne = true;
                int bodyEnd = FindBodyEnd(lines, i);
                int acquireLine = -1;
                for (int j = i; j <= bodyEnd; j++)
                {
                    if (AcquireCall.IsMatch(lines[j]))
                    {
                        acquireLine = j;
                        break;
                    }
                }

                if (acquireLine < 0)
                {
                    violations.Add($"{file}::{method}：未找到进闸点（方法已改名/搬移？本锁需同步修订）");
                    break;
                }

                if (!HasTopLevelCatch(lines, i, bodyEnd, afterLine: acquireLine))
                {
                    violations.Add($"{file}::{method}：进闸点之后没有顶层 catch —— "
                        + "异常路径上闸门/忙态会永久不释放（V11-A2/A3 回归）");
                }

                break;
            }

            if (checkedThisOne)
            {
                found.Add($"{file}::{method}");
            }
        }

        // 双向断言：既不许「清单里的进闸点消失」，也不许「进闸点无兜底」
        Assert.True(found.Count == ExpectedAcquireCallers.Length,
            "进闸点清单与代码不符（期望 " + ExpectedAcquireCallers.Length + " 处，实际命中 " + found.Count + " 处）："
            + Environment.NewLine + string.Join(Environment.NewLine, found));
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>反向验证（规则 6）：判据必须能区分「进闸后无兜底」与「进闸后有兜底」。</summary>
    [Fact]
    public void Judge_DistinguishesGuardedAndUnguardedAcquirePoints()
    {
        string[] unguarded =
        [
            "    private async Task InstallAsync(WingetPackageVm? pkg)",
            "    {",
            "        await AcquireOperationAsync();",
            "        await RunPackageOperationAsync(pkg);",
            "    }",
        ];
        Assert.False(HasTopLevelCatch(unguarded, 0, unguarded.Length - 1, afterLine: 2));

        string[] guarded =
        [
            "    private async Task InstallAsync(WingetPackageVm? pkg)",
            "    {",
            "        try",
            "        {",
            "            await AcquireOperationAsync();",
            "        }",
            "        catch (Exception ex)",
            "        {",
            "            ExitOperationOnFailure(\"安装\", ex);",
            "            return;",
            "        }",
            "        await RunPackageOperationAsync(pkg);",
            "    }",
        ];
        Assert.True(HasTopLevelCatch(guarded, 0, guarded.Length - 1, afterLine: 4));
    }

    // ── 判据助手（抽成静态方法：反向验证直接喂文本，无需真实文件） ──

    /// <summary>方法体末尾行（花括号深度配对；找不到闭合则取文件末行）。</summary>
    private static int FindBodyEnd(string[] lines, int sigLine)
    {
        int depth = 0;
        bool opened = false;
        for (int i = sigLine; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }
        }

        return lines.Length - 1;
    }

    /// <summary>
    /// <paramref name="afterLine"/> 之后是否存在**方法体顶层**（深度=1）的 catch。
    /// 与 <c>AsyncCommandCatchGuardTests</c> 同款深度判定：嵌套 lambda / 局部函数内的 catch 不算数
    /// （v6 O-1c 实证过"嵌套兜底骗过旧判据"这条路）。
    /// </summary>
    internal static bool HasTopLevelCatch(string[] lines, int sigLine, int bodyEnd, int afterLine)
    {
        int depth = 0;
        for (int i = sigLine; i <= bodyEnd; i++)
        {
            string line = lines[i];
            for (int c = 0; c < line.Length; c++)
            {
                if (line[c] == '{')
                {
                    depth++;
                }
                else if (line[c] == '}')
                {
                    depth--;
                }
                else if (i > afterLine && depth == 1
                    && c + 5 <= line.Length
                    && line.IndexOf("catch", c, StringComparison.Ordinal) == c)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ModuleDir()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return Path.Combine(d?.FullName ?? throw new InvalidOperationException("未找到仓库根"),
            "src/SystemToolkit.Modules.AppManager");
    }
}
