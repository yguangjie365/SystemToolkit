using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// AppManager「写操作可用性通知」覆盖锁（v15 核实，2026-09-14）。
/// <para>
/// 缺陷类：<c>CanOperate =&gt; !IsOperating &amp;&amp; !IsRefreshing</c> 是多个写命令的**共用判据**；
/// 而 CommunityToolkit.Mvvm 8.4.2 **无 <c>CommandManager</c> 兜底**（包文档 0 命中；探针实证
/// 未通知时 <c>IsEnabled</c> 双向卡死、<c>InvalidateRequerySuggested()</c> 也刷不动）⇒
/// 命令可用性**只**由 <c>[NotifyCanExecuteChangedFor]</c> 驱动。任一命令漏出通知列表 ⇒
/// 该按钮在「忙态 / 刷新态」窗口内**保持陈旧可点**，点下去撞 winget 进程互斥锁。
/// </para>
/// <para>
/// 🔴 判据为什么按**守卫字段**分别断言，而不是全局并集：<c>_isOperating</c> 与 <c>_isRefreshing</c>
/// 是**两个**独立触发源。若只断言"命令被**某处**通知覆盖"，则"只补一个字段、漏另一个"照样全绿
/// —— 而单边失灵正是此类缺陷的真实形态（刷新态失灵 ⇔ 忙态失灵，用户看到的是两组不同按钮卡死）。
/// 故本锁对 <c>CanOperate</c> 表达式里的**每个**守卫属性，各断言一次全覆盖。
/// </para>
/// <para>
/// 🔴 为什么既有守卫抓不到：<c>CommandCanExecuteRefreshGuardTests</c> **只看 <c>Selected*</c> 判据族**
/// （其源码自陈"<c>IsBusy</c> 之类赋值点已手动刷新"），<c>CanOperate</c> 不在覆盖面内。
/// v15 报告据此报了 **6 个漏项**，逐条回源核实后 **真漏 1 个**（<c>ExportInstalledCommand</c>），
/// 本轮已补 ⇒ 本锁防止再漏（含将来新增的 <c>CanOperate</c> 命令与新增的守卫字段）。
/// </para>
/// </summary>
public class AppManagerCanExecuteNotifyTests
{
    private static readonly Regex CanOperateUsage = new(
        @"\[RelayCommand\(CanExecute\s*=\s*nameof\(CanOperate\)\)\]", RegexOptions.Compiled);

    private static readonly Regex Signature = new(
        @"^\s*(?:private|public|internal|protected)\s+(?:async\s+)?[\w<>\[\]\.]+\s+(\w+)\s*\(", RegexOptions.Compiled);

    private static readonly Regex NotifyAttr = new(
        @"NotifyCanExecuteChangedFor\(nameof\((\w+)\)\)", RegexOptions.Compiled);

    /// <summary><c>private bool _isOperating;</c> 一类字段声明。</summary>
    private static readonly Regex FieldDecl = new(
        @"^\s*private\s+bool\s+(_\w+)\s*;", RegexOptions.Compiled);

    /// <summary><c>private bool CanOperate =&gt; !IsOperating &amp;&amp; !IsRefreshing;</c></summary>
    private static readonly Regex CanOperateExpr = new(
        @"CanOperate\s*=>\s*([^;]+);", RegexOptions.Compiled);

    [Fact]
    public void EveryGuardField_MustNotifyEveryCanOperateCommand()
    {
        string dir = ModuleDir();
        var commands = new HashSet<string>(StringComparer.Ordinal);
        var notifyByField = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var guards = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(dir, "*.cs"))
        {
            string[] lines = File.ReadAllLines(path);

            // ① 判据表达式里的守卫属性名（全模块只应在 AppManagerViewModel.cs 出现一处）
            foreach (string line in lines)
            {
                Match m = CanOperateExpr.Match(line);
                if (!m.Success)
                {
                    continue;
                }

                foreach (Match id in Regex.Matches(m.Groups[1].Value, @"[A-Za-z_]\w*"))
                {
                    if (!id.Value.Equals("CanOperate", StringComparison.Ordinal))
                    {
                        guards.Add(id.Value);
                    }
                }
            }

            // ② 以 CanOperate 为判据的命令（方法名 ⇒ 生成的命令属性名）
            for (int i = 0; i < lines.Length; i++)
            {
                if (!CanOperateUsage.IsMatch(lines[i]))
                {
                    continue;
                }

                for (int j = i + 1; j < Math.Min(i + 30, lines.Length); j++)
                {
                    Match sig = Signature.Match(lines[j]);
                    if (sig.Success)
                    {
                        commands.Add(CommandNameOf(sig.Groups[1].Value));
                        break;
                    }
                }
            }

            // ③ 每个布尔字段块上的 Notify 集合（属性行要紧贴在字段声明之前）
            for (int i = 0; i < lines.Length; i++)
            {
                Match f = FieldDecl.Match(lines[i]);
                if (!f.Success)
                {
                    continue;
                }

                var set = new HashSet<string>(StringComparer.Ordinal);
                for (int j = i - 1; j >= 0; j--)
                {
                    string t = lines[j].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (t.StartsWith('['))
                    {
                        foreach (Match a in NotifyAttr.Matches(lines[j]))
                        {
                            set.Add(a.Groups[1].Value);
                        }

                        continue;
                    }

                    break;
                }

                notifyByField[PropertyNameOf(f.Groups[1].Value)] = set;
            }
        }

        // 防恒绿：三项都要非空，否则判据形同虚设
        Assert.True(commands.Count >= 10,
            "CanOperate 命令只命中 " + commands.Count + " 个（期望 ≥ 10）—— 正则或目录失效，本锁形同虚设。");
        Assert.True(guards.Count >= 2,
            "CanOperate 表达式里只解析出 " + guards.Count + " 个守卫属性（期望 ≥ 2）—— 表达式改写或正则失效。");

        var violations = new List<string>();
        foreach (string guard in guards.OrderBy(g => g, StringComparer.Ordinal))
        {
            if (!notifyByField.TryGetValue(guard, out HashSet<string>? notified))
            {
                violations.Add("守卫字段 " + guard + "：找不到对应布尔字段块（字段名推断失配？本锁需同步修订）");
                continue;
            }

            foreach (string cmd in commands.Where(c => !notified.Contains(c)).OrderBy(c => c, StringComparer.Ordinal))
            {
                violations.Add(guard + " ⇒ " + cmd + "：未通知 ⇒ 该触发源变化时此按钮保持陈旧可点");
            }
        }

        Assert.True(violations.Count == 0,
            "以下 (守卫字段, 命令) 组合缺 NotifyCanExecuteChangedFor：" + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Select(v => "  " + v))
            + Environment.NewLine + "命令全集：" + string.Join(", ", commands.OrderBy(c => c, StringComparer.Ordinal)));
    }

    /// <summary>反向验证（规则 6）：判据必须能区分「每个字段都覆盖」与「只有其中一个字段覆盖」。</summary>
    [Fact]
    public void Judge_DistinguishesPerFieldCoverage()
    {
        string[] bothFields =
        [
            "    private bool CanOperate => !IsOperating && !IsRefreshing;",
            "    [RelayCommand(CanExecute = nameof(CanOperate))]",
            "    private async Task FooAsync()",
            "    [NotifyCanExecuteChangedFor(nameof(FooCommand))]",
            "    private bool _isOperating;",
            "    [NotifyCanExecuteChangedFor(nameof(FooCommand))]",
            "    private bool _isRefreshing;",
        ];
        Assert.Empty(GuardViolations(bothFields));

        // 只给 _isRefreshing 一处 ⇒ 必须点名 _isOperating 侧未覆盖
        string[] oneSided =
        [
            "    private bool CanOperate => !IsOperating && !IsRefreshing;",
            "    [RelayCommand(CanExecute = nameof(CanOperate))]",
            "    private async Task FooAsync()",
            "    [NotifyCanExecuteChangedFor(nameof(FooCommand))]",
            "    private bool _isRefreshing;",
        ];
        Assert.Contains(GuardViolations(oneSided), v => v.StartsWith("IsOperating", StringComparison.Ordinal));
    }

    // ── 判据助手（抽成静态方法：反向验证直接喂文本，无需真实文件） ──

    /// <summary>返回「(守卫字段, 命令) 未覆盖」清单，形如 <c>"IsOperating ⇒ FooCommand：未通知"</c>。</summary>
    internal static IReadOnlyList<string> GuardViolations(string[] lines)
    {
        var guards = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            Match m = CanOperateExpr.Match(line);
            if (!m.Success)
            {
                continue;
            }

            foreach (Match id in Regex.Matches(m.Groups[1].Value, @"[A-Za-z_]\w*"))
            {
                if (!id.Value.Equals("CanOperate", StringComparison.Ordinal))
                {
                    guards.Add(id.Value);
                }
            }
        }

        var commands = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!CanOperateUsage.IsMatch(lines[i]))
            {
                continue;
            }

            for (int j = i + 1; j < lines.Length; j++)
            {
                Match sig = Signature.Match(lines[j]);
                if (!sig.Success)
                {
                    continue;
                }

                commands.Add(CommandNameOf(sig.Groups[1].Value));
                break;
            }
        }

        var byField = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            Match f = FieldDecl.Match(lines[i]);
            if (!f.Success)
            {
                continue;
            }

            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int j = i - 1; j >= 0; j--)
            {
                string t = lines[j].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (t.StartsWith('['))
                {
                    foreach (Match a in NotifyAttr.Matches(lines[j]))
                    {
                        set.Add(a.Groups[1].Value);
                    }

                    continue;
                }

                break;
            }

            byField[PropertyNameOf(f.Groups[1].Value)] = set;
        }

        var result = new List<string>();
        foreach (string guard in guards.OrderBy(g => g, StringComparer.Ordinal))
        {
            if (!byField.TryGetValue(guard, out HashSet<string>? notified))
            {
                result.Add(guard + " ⇒ （找不到字段块）");
                continue;
            }

            foreach (string cmd in commands.Where(c => !notified.Contains(c)).OrderBy(c => c, StringComparer.Ordinal))
            {
                result.Add(guard + " ⇒ " + cmd + "：未通知");
            }
        }

        return result;
    }

    /// <summary>方法名 ⇒ 生成的命令属性名（<c>InstallAsync</c> ⇒ <c>InstallCommand</c>）。</summary>
    internal static string CommandNameOf(string method)
    {
        string name = method.EndsWith("Async", StringComparison.Ordinal) ? method[..^5] : method;
        return name + "Command";
    }

    /// <summary>字段名 ⇒ 生成的属性名（<c>_isOperating</c> ⇒ <c>IsOperating</c>）。</summary>
    internal static string PropertyNameOf(string field)
    {
        string bare = field.TrimStart('_');
        return char.ToUpperInvariant(bare[0]) + bare[1..];
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
