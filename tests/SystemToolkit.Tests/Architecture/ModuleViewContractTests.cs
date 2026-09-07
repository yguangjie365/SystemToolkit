using System.Text.RegularExpressions;

namespace SystemToolkit.Tests;

/// <summary>
/// 模块视图契约守卫（F-1 防回归，2026-09-06）。
/// <para>
/// 背景：Shell 原先按 <c>nav.Module.Id == "xxx"</c> 硬编码 8 个分支决定加载哪个 View，
/// 新增/改名模块必须同步改 Shell，漏改会静默退化成「建设中」占位页，且三套既有守卫全都拦不住。
/// 现改为 <c>IModule.CreateView(IServiceProvider)</c> 由模块自己返回视图。
/// </para>
/// <para>
/// 本守卫堵住新方案的唯一退化路径：<b>模块忘了覆写 CreateView</b>——
/// 基类默认返回 null，宿主就会显示占位页（与旧事故同款「静默失败」）。
/// 判定依据：模块工程里只要存在 <c>*View.xaml</c>，该模块的 <c>*Module.cs</c> 就必须覆写 CreateView。
/// </para>
/// </summary>
public class ModuleViewContractTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName
                ?? throw new InvalidOperationException("未找到仓库根（Directory.Build.props）");
        }
    }

    [Fact]
    public void Module_WithViewXaml_MustOverrideCreateView()
    {
        string modulesRoot = Path.Combine(RepoRoot, "src");
        string[] moduleDirs = Directory.GetDirectories(modulesRoot, "SystemToolkit.Modules.*")
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(moduleDirs);

        var offenders = new List<string>();
        var checkedModules = new List<string>();

        foreach (string dir in moduleDirs)
        {
            string[] views = Directory.GetFiles(dir, "*View.xaml", SearchOption.TopDirectoryOnly);
            if (views.Length == 0)
            {
                continue; // 占位模块：没有 View 也不用覆写 CreateView
            }

            string[] moduleFiles = Directory.GetFiles(dir, "*Module.cs", SearchOption.TopDirectoryOnly);
            if (moduleFiles.Length == 0)
            {
                offenders.Add($"{Path.GetFileName(dir)}：有 View 却没有 *Module.cs");
                continue;
            }

            foreach (string moduleFile in moduleFiles)
            {
                string text = File.ReadAllText(moduleFile);
                checkedModules.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(moduleFile)}");

                // 必须出现 `override … CreateView(`；只写注释不算
                if (!Regex.IsMatch(text, @"override\s+object\??\s+CreateView\s*\("))
                {
                    offenders.Add($"{Path.GetFileName(dir)}/{Path.GetFileName(moduleFile)}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下模块工程存在 *View.xaml，但对应的 *Module.cs 未覆写 CreateView —— " +
            "宿主会静默显示「建设中」占位页（F-1 事故的退化形态）：" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Select(o => "  · " + o)) + Environment.NewLine
            + "修法：在模块类中加" + Environment.NewLine
            + "    public override object CreateView(IServiceProvider services) => services.GetRequiredService<XxxView>();");
    }

    /// <summary>
    /// 反向验证：Shell 主窗口不得再出现按模块 Id 分派视图的硬编码分支。
    /// 这是 F-1 的直接判据——出现即说明有人又绕回了旧写法。
    /// </summary>
    [Fact]
    public void Shell_MustNotDispatchViewsByModuleId()
    {
        string mainWindow = Path.Combine(RepoRoot, "src", "SystemToolkit.Shell", "MainWindow.xaml.cs");
        Assert.True(File.Exists(mainWindow), $"缺少文件：{mainWindow}");

        string text = File.ReadAllText(mainWindow);
        MatchCollection matches = Regex.Matches(text, @"Module\.Id\s*==\s*""[^""]+""");

        Assert.True(
            matches.Count == 0,
            $"MainWindow.xaml.cs 仍存在 {matches.Count} 处按模块 Id 硬编码的分派分支（F-1 回归）："
            + string.Join(", ", matches.Select(m => m.Value))
            + "。视图分派必须走 IModule.CreateView。");
    }
}
