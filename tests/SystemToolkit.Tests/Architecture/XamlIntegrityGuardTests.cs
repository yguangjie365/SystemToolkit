using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// XAML 完整性守卫（规则 6 沉淀，2026-09-04 事故）：
/// 调试用最小探针 XAML 被误提交并随构建交付，驱动管理页上线即空白。
/// 本守卫保证：模块视图必须包含页面关键标记，且不得含任何探针/占位残留。
/// <para>
/// 🟡 2026-09-14（v8-🟡-10）：原先只覆盖 3 个视图，其余 6 个 <c>*View.xaml</c> 的
/// "页面标记 + 探针残留"**从未受检**（与 🟠-7 的命令绑定守卫同源的名单制覆盖缺口）。
/// 现改为：标记表仍逐页登记（"这页该有什么"是**数据**），但**完整性由
/// <see cref="MarkerTable_CoversEveryViewXaml"/> 兜底**——新增视图忘了登记就直接变红，
/// 不再可能静默漏检。
/// </para>
/// <para>
/// ⚠️ 禁词判据是**整词**匹配（<c>\bprobe\b</c>）而非子串：扩围到 9 个视图后实测抓到
/// <c>NetManagerView.xaml</c> 里的合法标识符 <c>ProbeMtuCommand</c>（MTU 探测）被子串判据误报。
/// 整词匹配既能抓住独立的探针残留，又不会把正常命令名算进去。
/// </para>
/// </summary>
public class XamlIntegrityGuardTests
{
    /// <summary>探针/占位禁词（全视图统一；出现即视为调试版本混入交付物）。</summary>
    private const string ForbiddenProbeResidue = "probe";

    /// <summary>禁词判据：**整词**匹配（理由见类注释）。</summary>
    private static readonly Regex ProbeResiduePattern =
        new(@"\b" + ForbiddenProbeResidue + @"\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 页面身份标记表：模块目录 → 视图文件 → 必含标记。
    /// <para>
    /// 标记取自各页**可见的标题/副标题字面量**（唯一能区分"真页面"与"占位页"的稳定特征）。
    /// 短标题（如「设置」）用其副标题以免误判。
    /// </para>
    /// <para>🔴 这不是覆盖白名单——新增视图必须在此登记，否则 <see cref="MarkerTable_CoversEveryViewXaml"/> 报红。</para>
    /// </summary>
    private static readonly (string ModuleDir, string ViewFile, string MustContain)[] ViewMarkers =
    {
        ("SystemToolkit.Modules.DriverManager", "DriverManagerView.xaml", "DRIVER MANAGER"),
        ("SystemToolkit.Modules.AppManager", "AppManagerView.xaml", "APP MANAGER"),
        ("SystemToolkit.Modules.Overview", "OverviewView.xaml", "本机概览"),
        ("SystemToolkit.Modules.FileTransfer", "FileTransferView.xaml", "文件互传"),
        ("SystemToolkit.Modules.FileBackup", "FileBackupView.xaml", "文件备份"),
        ("SystemToolkit.Modules.GameManager", "GameManagerView.xaml", "游戏管理"),
        ("SystemToolkit.Modules.MusicManager", "MusicManagerView.xaml", "音乐管理"),
        ("SystemToolkit.Modules.NetManager", "NetManagerView.xaml", "网络管理"),
        // 标题仅两个字（"设置"）会与"设置"这类普通词混淆，改用副标题
        ("SystemToolkit.Modules.Settings", "SettingsView.xaml", "SETTINGS · 全局配置集中管理"),
    };

    public static TheoryData<string, string, string> ViewMarkerData
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach ((string moduleDir, string viewFile, string mustContain) in ViewMarkers)
            {
                data.Add(moduleDir, viewFile, mustContain);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ViewMarkerData))]
    public void XamlGuard_Views_MustContainPageMarker_AndNoProbeResidue(
        string moduleDir, string viewFile, string mustContain)
    {
        string path = Path.Combine(RepoRoot, "src", moduleDir, viewFile);
        Assert.True(File.Exists(path), $"视图文件不存在：{path}");

        string content = File.ReadAllText(path);
        Assert.True(content.Contains(mustContain, StringComparison.OrdinalIgnoreCase),
            $"{viewFile} 缺少页面身份标记「{mustContain}」——可能被探针/占位版本覆盖（2026-09-04 事故：驱动页上线即空白）");
        Assert.False(ProbeResiduePattern.IsMatch(content),
            $"{viewFile} 含调试残留「{ForbiddenProbeResidue}」——探针版本不得进入交付物");
    }

    /// <summary>
    /// 🟡 审查 v8-🟡-10：标记表必须覆盖 <c>src</c> 下**全部** <c>*View.xaml</c>。
    /// <para>
    /// 这条是 fail-safe 的关键：没有它，标记表就退化成"只检名单内的"——新增视图静默不受检。
    /// 反向验证：临时新增一个 <c>XxxView.xaml</c>（或删掉表中一行）→ 本用例变红。
    /// </para>
    /// </summary>
    [Fact]
    public void MarkerTable_CoversEveryViewXaml()
    {
        var declared = ViewMarkers
            .Select(m => $"{m.ModuleDir}/{m.ViewFile}")
            .ToHashSet(StringComparer.Ordinal);

        string[] actual = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src"), "*View.xaml", SearchOption.AllDirectories)
            .Where(p => !ViewLoadSmokeGuardTests.IsBuildArtifactPath(p))
            .Select(p => Path.GetRelativePath(RepoRoot, p).Replace('\\', '/'))
            .Select(rel =>
            {
                string dir = Path.GetDirectoryName(rel)!.Replace('\\', '/');
                return $"{dir[(dir.LastIndexOf('/') + 1)..]}/{Path.GetFileName(rel)}";
            })
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        string[] missing = actual.Where(a => !declared.Contains(a)).ToArray();
        string[] stale = declared.Where(d => !actual.Contains(d, StringComparer.Ordinal)).ToArray();

        Assert.True(
            missing.Length == 0,
            "以下视图未登记页面身份标记（新增视图请补进 XamlIntegrityGuardTests.ViewMarkers）：\n"
            + string.Join("\n", missing));
        Assert.True(
            stale.Length == 0,
            "标记表登记了不存在的视图（已删除/改名？请同步清理）：\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// 反向验证自检：禁词判据抓得住**独立的** probe 残留，放得过合法标识符。
    /// <para>这条钉的是判据本身——只断言 9 个视图全绿，无法排除"判据恒为假"。</para>
    /// </summary>
    [Fact]
    public void ProbeResiduePattern_WholeWord_ReverseVerification()
    {
        Assert.Matches(ProbeResiduePattern, "<UserControl x:Name=\"probe\">");
        Assert.Matches(ProbeResiduePattern, "<!-- probe 临时最小页 -->");
        Assert.Matches(ProbeResiduePattern, "Probe");

        // 合法标识符不得被误报（扩围后实测踩中的那一个）
        Assert.DoesNotMatch(ProbeResiduePattern, "{Binding Diagnostics.ProbeMtuCommand}");
        Assert.DoesNotMatch(ProbeResiduePattern, "Probing");
    }

    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未找到仓库根目录");
    }
}
