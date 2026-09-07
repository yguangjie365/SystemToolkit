namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// XAML 完整性守卫（规则 6 沉淀，2026-09-04 事故）：
/// 调试用最小探针 XAML 被误提交并随构建交付，驱动管理页上线即空白。
/// 本守卫保证：模块视图必须包含页面关键标记，且不得含任何探针/占位残留。
/// </summary>
public class XamlIntegrityGuardTests
{
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

    public static TheoryData<string, string, string, string> ViewMarkers => new()
    {
        // 模块目录, 视图文件, 必含标记（页面身份特征）, 探针/占位禁词
        { "SystemToolkit.Modules.DriverManager", "DriverManagerView.xaml", "DRIVER MANAGER", "probe" },
        { "SystemToolkit.Modules.AppManager", "AppManagerView.xaml", "APP MANAGER", "probe" },
        { "SystemToolkit.Modules.Overview", "OverviewView.xaml", "本机概览", "probe" },
    };

    [Theory]
    [MemberData(nameof(ViewMarkers))]
    public void XamlGuard_Views_MustContainPageMarker_AndNoProbeResidue(string moduleDir, string viewFile, string mustContain, string forbidden)
    {
        string path = Path.Combine(RepoRoot, "src", moduleDir, viewFile);
        Assert.True(File.Exists(path), $"视图文件不存在：{path}");

        string content = File.ReadAllText(path);
        Assert.True(content.Contains(mustContain, StringComparison.OrdinalIgnoreCase),
            $"{viewFile} 缺少页面身份标记「{mustContain}」——可能被探针/占位版本覆盖（2026-09-04 事故：驱动页上线即空白）");
        Assert.False(content.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"{viewFile} 含调试残留「{forbidden}」——探针版本不得进入交付物");
    }
}
