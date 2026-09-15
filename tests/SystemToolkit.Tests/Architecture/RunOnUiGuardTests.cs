using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 「RunOnUi 编组入口 · 5 处同批」契约锁（X-2 配套，2026-09-15）。
/// <para>
/// 背景：<c>RunOnUi(Action)</c> 的 <c>d is null</c> 分支在 5 个模块里语义相同——**直执行**（不拒绝、不落日志）。
/// 两批审查各自独立报出「null 时后台线程直改 UI 集合」，P0 核实后判定**保持现状**：
/// ① 生产路径 <c>Application.Current?.Dispatcher</c> 在 <c>App.OnStartup</c>（UI 线程）内注入，非 null；
/// ② <c>SystemToolkit.Worker</c> 是 stub，不构造模块 VM；③ 全仓 **9 处测试宿主刻意传 null**，
/// 拒绝执行会让 VM 状态永不更新（用例无从断言）。
/// </para>
/// <para>
/// 🔴 本锁守的不是"必须落日志"（那与既定裁定矛盾），而是 **「5 处必须同批同改」** 这个不变量：
/// 实现总数恒为 5、每处都带 X-2 契约注记、null 分支都仍直执行。将来的正解（worker 化落地时）是
/// "拒绝执行 + 落日志 + **同时**给测试宿主一条有 Dispatcher 的通道"——它必须改到全部 5 处，只改一处即红。
/// </para>
/// <para>
/// ⚠️ 为什么不比"文本逐字相同"：<c>GameManagerViewModel</c> 的写法是 <c>if (… || d.CheckAccess())</c>
/// （把 CheckAccess 并进首个 if、且 <c>_ = d.BeginInvoke(...)</c>），与其余 4 处的
/// <c>if (…) … else if (CheckAccess)</c> **语义等价但文本不同**（属既有实现差异）⇒ 逐字比对会当场误报。
/// </para>
/// </summary>
public class RunOnUiGuardTests
{
    /// <summary>期望的实现清单——新增/删除任一处都必须显式改这里（第 1 个用例会双向断言）。</summary>
    private static readonly string[] ExpectedImplFiles =
    [
        "src/SystemToolkit.Modules.FileTransfer/FileTransferDesktopViewModel.cs",
        "src/SystemToolkit.Modules.FileTransfer/FileTransferMobileViewModel.cs",
        "src/SystemToolkit.Modules.GameManager/GameManagerViewModel.cs",
        "src/SystemToolkit.Modules.MusicManager/MusicManagerViewModel.cs",
        "src/SystemToolkit.Modules.NetManager/SplitRouteTabViewModel.cs",
    ];

    private static readonly Regex ImplSignature =
        new(@"private\s+void\s+RunOnUi\s*\(\s*Action\s+\w+\s*\)", RegexOptions.Compiled);

    [Fact]
    public void RunOnUiImplementations_AreExactlyTheExpectedFive()
    {
        string[] found = FindRunOnUiImplementations();
        Assert.True(found.Length == ExpectedImplFiles.Length,
            "RunOnUi 实现数 = " + found.Length + "（期望 " + ExpectedImplFiles.Length + "）："
            + Environment.NewLine + string.Join(Environment.NewLine, found));
        Assert.Equal(
            ExpectedImplFiles.OrderBy(x => x, StringComparer.Ordinal),
            found.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryRunOnUi_KeepsNullBranchDirectExecuting_AndCarriesX2ContractNote()
    {
        var failures = new List<string>();
        foreach (string rel in ExpectedImplFiles)
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar)));
            string body = ExtractRunOnUiBody(text);
            if (body.Length == 0)
            {
                failures.Add(rel + "：未找到 RunOnUi 方法体（改名/搬移？本锁需同步修订）");
                continue;
            }

            if (!body.Contains("d is null", StringComparison.Ordinal))
            {
                failures.Add(rel + "：方法体内未找到 `d is null` 判据（本锁需同步修订）");
            }

            if (NullBranchRejectsInsteadOfExecuting(body))
            {
                failures.Add(rel + "：null 分支已变成「拒绝执行」——X-2 的裁定要求 5 处**同批同改**；"
                    + "若确要改（worker 化落地），必须同时给 9 处测试宿主一条有 Dispatcher 的通道，并同步更新本锁");
            }

            if (!body.Contains("X-2", StringComparison.Ordinal))
            {
                failures.Add(rel + "：方法体缺少 X-2 契约注记（5 处同款的决策留痕；删掉即失去「改一处须同改五处」的提醒）");
            }

            if (!body.Contains("BeginInvoke", StringComparison.Ordinal))
            {
                failures.Add(rel + "：未找到 BeginInvoke（跨线程编组路径缺失？）");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>反向验证（03 §4.1）：判据必须能区分「null 分支直执行」与「null 分支拒绝执行」。</summary>
    [Fact]
    public void Judge_NullBranchDetector_DistinguishesDirectExecutingFromRejecting()
    {
        string directExecute = """
            var d = _dispatcher;
            if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive)
            {
                RunGuarded(action);
            }
            else if (d.CheckAccess())
            {
                RunGuarded(action);
            }
            else
            {
                d.BeginInvoke(() => RunGuarded(action));
            }
            """;
        Assert.False(NullBranchRejectsInsteadOfExecuting(directExecute));

        string reject = """
            var d = _dispatcher;
            if (d is null || d.HasShutdownStarted || !d.Thread.IsAlive)
            {
                _logger.Warn("无 UI Dispatcher，跳过一次界面更新");
                return;
            }
            else if (d.CheckAccess())
            {
                RunGuarded(action);
            }
            else
            {
                d.BeginInvoke(() => RunGuarded(action));
            }
            """;
        Assert.True(NullBranchRejectsInsteadOfExecuting(reject));
    }

    // ── 判据助手（抽成 internal static：反向验证可直接喂文本，无需真实文件） ──

    /// <summary>
    /// null 分支是否"拒绝执行"：判据 = <c>d is null</c> 之后、**第一次** <c>RunGuarded</c> 之前出现 <c>return</c>。
    /// （直执行分支必然是"判 null → 立刻 RunGuarded"，中间不可能有 return。）
    /// </summary>
    internal static bool NullBranchRejectsInsteadOfExecuting(string body)
    {
        int nullIdx = body.IndexOf("d is null", StringComparison.Ordinal);
        if (nullIdx < 0)
        {
            return false;
        }

        int guardedIdx = body.IndexOf("RunGuarded", nullIdx, StringComparison.Ordinal);
        if (guardedIdx < 0)
        {
            return true;   // 连 RunGuarded 都没有 ⇒ 无法认定为"直执行"
        }

        int returnIdx = body.IndexOf("return", nullIdx, StringComparison.Ordinal);
        return returnIdx >= 0 && returnIdx < guardedIdx;
    }

    /// <summary>取 <c>RunOnUi</c> 方法体（含花括号，按深度配对截取）。</summary>
    internal static string ExtractRunOnUiBody(string text)
    {
        Match signature = ImplSignature.Match(text);
        if (!signature.Success)
        {
            return "";
        }

        int open = text.IndexOf('{', signature.Index + signature.Length);
        if (open < 0)
        {
            return "";
        }

        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[open..(i + 1)];
                }
            }
        }

        return "";
    }

    private static string[] FindRunOnUiImplementations()
    {
        var hits = new List<string>();
        foreach (string path in Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (ViewLoadSmokeGuardTests.IsBuildArtifactPath(path))
            {
                continue;
            }

            if (ImplSignature.IsMatch(File.ReadAllText(path)))
            {
                hits.Add(Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/'));
            }
        }

        hits.Sort(StringComparer.Ordinal);
        return [.. hits];
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根（SystemToolkit.sln）");
    }
}
