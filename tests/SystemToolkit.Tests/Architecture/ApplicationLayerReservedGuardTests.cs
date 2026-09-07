namespace SystemToolkit.Tests;

/// <summary>
/// Application 层「预留未使用」守卫（2026-09-06 用户裁定，AGENTS.md §二「Application 层状态」）。
/// <para>
/// 背景：审查报告（reviewer-2, F-3）指出 <c>src/SystemToolkit.Application/</c> 是空 csproj，
/// 与 AGENTS.md「跨模块编排（如重装助手）」的定位存在兑现差距。用户裁定结论为
/// <b>暂标记预留、不提前设计抽象</b>——需求未定稿就出接口，真开工时会被推翻返工。
/// </para>
/// <para>
/// 本守卫把该裁定变成机器约束：<b>往这一层塞任何 .cs 文件都会让测试变红</b>，
/// 红的时候必须按消息里的步骤走（先改 AGENTS.md 状态段 + 本文件的解除说明，再删本守卫），
/// 而不是绕过或无视。做到「预留」是显式状态而非默认状态。
/// </para>
/// </summary>
public class ApplicationLayerReservedGuardTests
{
    private const string Project = "SystemToolkit.Application";

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

    private static string AppLayerDir => Path.Combine(RepoRoot, "src", Project);

    /// <summary>
    /// 预留期：Application 层不得含任何源代码文件（obj/bin 等生成物不算）。
    /// </summary>
    [Fact]
    public void ApplicationLayer_IsStillEmpty_WhileReserved()
    {
        string dir = AppLayerDir;
        Assert.True(Directory.Exists(dir), $"缺少目录：{dir}");

        string[] sources = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

        Assert.True(
            sources.Length == 0,
            """
            SystemToolkit.Application 仍处于「预留未使用」状态，但已检测到源代码文件：
            """ + string.Join(Environment.NewLine, sources.Select(p => "  · " + Path.GetRelativePath(RepoRoot, p))) +
            """

            如果你正在 V1.0 重装助手（或任何跨模块编排）落地本层——很好，请按顺序完成：
              1. 修改 AGENTS.md §二「Application 层状态」：把「预留（reserved）」改为「已启用」，
                 写明本层承担的第一个跨模块调用是什么；
              2. 删除本测试文件（ApplicationLayerReservedGuardTests.cs），它的使命已完成；
              3. 若新增了依赖边，同步 DependencyGuardTests 白名单。
            如果你只是在试探——请不要绕过本守卫：跨模块逻辑在此之前一律不放这一层。
            """);
    }

    /// <summary>
    /// 预留层仍须被解决方案与依赖白名单登记（否则会变成"忘了的死角"而非"有意的预留"）。
    /// </summary>
    [Fact]
    public void ApplicationLayer_IsRegisteredInDependencyWhitelist()
    {
        string csproj = Path.Combine(AppLayerDir, $"{Project}.csproj");
        Assert.True(File.Exists(csproj), $"缺少项目文件：{csproj}");

        string guard = File.ReadAllText(Path.Combine(RepoRoot, "tests", "SystemToolkit.Tests",
            "Architecture", "DependencyGuardTests.cs"));
        Assert.Contains(Project, guard, StringComparison.Ordinal);
    }
}
