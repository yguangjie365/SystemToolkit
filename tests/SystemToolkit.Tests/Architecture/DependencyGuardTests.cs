using System.Text.RegularExpressions;

namespace SystemToolkit.Tests;

/// <summary>
/// 架构守卫（02 分册规则 1-6 的机器拦截）。
/// 直接读取 csproj 文本判定依赖方向，规则违规即测试红。
/// 新增项目必须先在本文件白名单登记（规则 5），否则 <see cref="DependencyGuard_ProjectEdgesMatchWhitelist"/> 红。
/// </summary>
public class DependencyGuardTests
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

    private static string SrcPath(string project) =>
        Path.Combine(RepoRoot, "src", project, $"{project}.csproj");

    private static List<(string From, string To)> ReadProjectReferences(string project)
    {
        string file = SrcPath(project);
        Assert.True(File.Exists(file), $"缺少项目文件：{file}");
        string text = File.ReadAllText(file);
        MatchCollection matches = Regex.Matches(text, @"<ProjectReference Include=""([^""]+)""");
        return matches
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace("\\", Path.DirectorySeparatorChar.ToString())))
            .Select(to => (project, to))
            .ToList();
    }

    private static string TargetFramework(string project)
    {
        string text = File.ReadAllText(SrcPath(project));
        return Regex.Match(text, @"<TargetFramework>([^<]+)</TargetFramework>").Groups[1].Value;
    }

    // ── 白名单：02 分册 §二 项目清单。新增边必须先改规范再改这里。──

    private static readonly string[] Modules =
    [
        "SystemToolkit.Modules.Overview",
        "SystemToolkit.Modules.AppManager",
        "SystemToolkit.Modules.DriverManager",
        "SystemToolkit.Modules.FileBackup",
        "SystemToolkit.Modules.FileTransfer",
        "SystemToolkit.Modules.NetManager",
        "SystemToolkit.Modules.RecoveryManager",
        "SystemToolkit.Modules.GameManager",
        "SystemToolkit.Modules.MusicManager",
        "SystemToolkit.Modules.Settings",
    ];

    private static readonly Dictionary<string, string[]> AllowedEdges = new()
    {
        ["SystemToolkit.Shell"] =
        [
            "SystemToolkit.UI.Common",
            "SystemToolkit.Application",
            "SystemToolkit.Infrastructure",
            "SystemToolkit.ElevatedHelper", // V0.3-B：Shell 引用提权进程工程，保证 Helper.exe 随主程序输出
            .. Modules,
        ],
        ["SystemToolkit.UI.Common"] = ["SystemToolkit.Core", "SystemToolkit.Abstractions"],
        ["SystemToolkit.Application"] =
        [
            "SystemToolkit.Core",
            "SystemToolkit.Abstractions",
            "SystemToolkit.Infrastructure",
        ],
        ["SystemToolkit.Infrastructure"] = ["SystemToolkit.Core", "SystemToolkit.Abstractions"],
        ["SystemToolkit.ElevatedHelper"] = ["SystemToolkit.Core", "SystemToolkit.Abstractions"],
        ["SystemToolkit.Worker"] = ["SystemToolkit.Core", "SystemToolkit.Infrastructure"],
    };

    [Fact]
    public void DependencyGuard_Rule2_CoreAndAbstractionsHaveNoProjectReferences()
    {
        foreach (string project in new[] { "SystemToolkit.Core", "SystemToolkit.Abstractions" })
        {
            List<(string From, string To)> refs = ReadProjectReferences(project);
            Assert.True(refs.Count == 0, $"规则 2 违规：{project} 不得引用任何项目（实际 {refs.Count} 条）");
            Assert.True(TargetFramework(project) == "net10.0",
                $"规则 2 违规：{project} 必须为跨平台 net10.0（实际 {TargetFramework(project)}）");
        }
    }

    [Fact]
    public void DependencyGuard_ModuleDependencies_WithinWhitelist()
    {
        // 模块可引用 Core / Abstractions / UI.Common（共享控件与令牌）；白名单外交错即红
        var allowed = new HashSet<string> { "SystemToolkit.Core", "SystemToolkit.Abstractions", "SystemToolkit.UI.Common" };
        foreach (string module in Modules)
        {
            var refs = ReadProjectReferences(module).Select(r => r.To).ToHashSet();
            var unexpected = refs.Except(allowed).ToList();
            Assert.True(unexpected.Count == 0,
                $"规则违规：{module} 出现白名单之外的引用：{string.Join(", ", unexpected)}");
        }
    }

    [Fact]
    public void DependencyGuard_Rule3_ModulesDoNotReferenceEachOther()
    {
        foreach (string module in Modules)
        {
            List<(string From, string To)> refs = ReadProjectReferences(module);
            var cross = refs
                .Where(r => r.To.StartsWith("SystemToolkit.Modules.", StringComparison.Ordinal))
                .ToList();
            Assert.True(cross.Count == 0,
                $"规则 3 违规：{module} 引用了其他模块：{string.Join(", ", cross.Select(r => r.To))}");
        }
    }

    [Fact]
    public void DependencyGuard_ProjectEdgesMatchWhitelist()
    {
        foreach ((string project, string[] allowed) in AllowedEdges)
        {
            var refs = ReadProjectReferences(project).Select(r => r.To).ToHashSet();
            var unexpected = refs.Except(allowed).ToList();
            var missing = allowed.Except(refs).ToList();

            Assert.True(unexpected.Count == 0,
                $"规则 1/5 违规：{project} 出现白名单之外的引用：{string.Join(", ", unexpected)}。" +
                "新增依赖须先更新 02-架构与依赖规范 §二 并登记本白名单");
            Assert.True(missing.Count == 0,
                $"{project} 缺少规范要求的引用：{string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void DependencyGuard_Rule6_BuildServiceProviderOnlyInHostProjects()
    {
        string[] allowedHosts = new[] { "SystemToolkit.Shell", "SystemToolkit.ElevatedHelper", "SystemToolkit.Worker" };
        string srcRoot = Path.Combine(RepoRoot, "src");
        var violations = new List<string>();

        foreach (string projectDir in Directory.EnumerateDirectories(srcRoot))
        {
            string project = Path.GetFileName(projectDir);
            if (allowedHosts.Contains(project))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                if (File.ReadAllText(file).Contains("BuildServiceProvider"))
                {
                    violations.Add(file);
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"规则 6 违规：非宿主项目调用 BuildServiceProvider：\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void DependencyGuard_MainAppManifest_ForbidsRequireAdministrator()
    {
        string manifest = File.ReadAllText(Path.Combine(RepoRoot, "src", "SystemToolkit.Shell", "app.manifest"));
        // 精确判定 XML 属性值，避免注释文字误报
        Assert.DoesNotContain("level=\"requireAdministrator\"", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DependencyGuard_ShellModuleManifest_MatchesModuleProjects()
    {
        // AppRoot.KnownPages 同步教训：模块清单漏登记会静默失效，这里机器校验
        string appCs = File.ReadAllText(Path.Combine(RepoRoot, "src", "SystemToolkit.Shell", "App.xaml.cs"));

        // 正向：Modules 白名单中的每个模块都必须在 KnownModules 中注册
        foreach (string module in Modules)
        {
            Assert.True(appCs.Contains(module, StringComparison.Ordinal),
                $"Shell 的 KnownModules 缺少 {module}（新增模块须同步登记到 DependencyGuard 白名单和 KnownModules 列表）");
        }

        // 反向：KnownModules 中注册的模块必须都在 Modules 白名单中（防止遗漏守卫）
        string[] knownModuleTypes =
        [
            "Overview", "AppManager", "DriverManager", "FileBackup", "FileTransfer",
            "NetManager", "RecoveryManager", "GameManager", "MusicManager", "Settings"
        ];

        foreach (string moduleName in knownModuleTypes)
        {
            string fullModuleName = $"SystemToolkit.Modules.{moduleName}";
            Assert.True(Modules.Contains(fullModuleName),
                $"{fullModuleName} 已在 KnownModules 中注册但未加入 DependencyGuard 白名单（需同步添加）");
        }
    }
}
