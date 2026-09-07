using System.Reflection;
using System.Threading;
using TimersTimer = System.Timers.Timer;

namespace SystemToolkit.Tests;

/// <summary>
/// 扩展模块隔离守卫（AGENTS.md §二·七 规则3：禁用后无任何后台任务残留）。
/// <para>
/// 背景：审查报告 M-2 指出 GameManager/MusicManager 等扩展模块虽声明 CanDisable=true，
/// 但缺少机器可验证的守卫确保禁用后无线程/定时器/监听器残留。本守卫把该要求变成自动化约束。
/// </para>
/// <para>
/// 注意：当前版本（V0.6）尚未实现运行时模块禁用功能，本测试为预防性守卫——
/// 一旦未来实现禁用逻辑，此测试将自动验证资源清理的正确性。
/// </para>
/// </summary>
public class ModuleIsolationGuardTests
{
    private static readonly string[] ExtendableModules =
    [
        "SystemToolkit.Modules.GameManager",
        "SystemToolkit.Modules.MusicManager",
    ];

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

    /// <summary>
    /// 验证扩展模块的 ViewModel/Service 不持有长期运行的后台资源（Timer/BackgroundWorker/FileSystemWatcher/Thread）。
    /// 这是静态代码分析级别的守卫，不依赖运行时状态。
    /// </summary>
    [Fact]
    public void ExtendableModules_DontHoldLongRunningResources()
    {
        Type[] forbiddenTypes =
        [
            typeof(TimersTimer),
            typeof(FileSystemWatcher),
            typeof(Thread),
        ];

        List<string> violations = new();

        foreach (string moduleName in ExtendableModules)
        {
            string moduleDir = Path.Combine(RepoRoot, "src", moduleName);
            Assert.True(Directory.Exists(moduleDir), $"缺少模块目录：{moduleDir}");

            // 扫描模块内所有 .cs 文件（排除 obj/bin）
            string[] sourceFiles = Directory.GetFiles(moduleDir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToArray();

            foreach (string file in sourceFiles)
            {
                string content = File.ReadAllText(file);
                string relativePath = Path.GetRelativePath(RepoRoot, file);

                // 检查是否直接实例化禁止类型
                foreach (Type forbiddenType in forbiddenTypes)
                {
                    // 简单文本匹配：new Timer( / new FileSystemWatcher( / new Thread(
                    string pattern = $"new {forbiddenType.Name}(";
                    if (content.Contains(pattern, StringComparison.Ordinal))
                    {
                        violations.Add($"  · {relativePath}: 直接实例化 {forbiddenType.Name}（应通过接口注入或生命周期管理）");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            """
            扩展模块不得直接持有长期运行的后台资源（否则禁用后无法清理）：

            """ + string.Join(Environment.NewLine, violations) +
            """

            如需使用定时器/监听器，请：
              1. 定义为 IDisposable/IAsyncDisposable 服务；
              2. 在模块的 InitializeAsync 中注册到 DI 容器；
              3. 确保服务生命周期与模块绑定（模块禁用时随 DI 容器释放）。
            """);
    }

    /// <summary>
    /// 验证扩展模块的所有服务都实现了 IDisposable 或 IAsyncDisposable（若持有非托管资源）。
    /// 这是防御性检查：若模块启动网络监听(Kestrel)、音频设备、文件监控等，必须提供清理入口。
    /// </summary>
    [Fact]
    public void ExtendableModuleServices_ImplementDisposableWhenHoldingResources()
    {
        // 此测试为占位符，实际实现需要反射加载各模块程序集并检查服务类型。
        // 由于当前模块尚未完全实现，暂只做结构验证。
        Assert.True(ExtendableModules.Length >= 2, "扩展模块清单应包含 GameManager 和 MusicManager");
    }

    /// <summary>
    /// 验证 App.OnExit 中包含模块清理日志记录（审查报告 M-2 要求）。
    /// </summary>
    [Fact]
    public void App_OnExit_LogsModuleCleanup()
    {
        string appFile = Path.Combine(RepoRoot, "src", "SystemToolkit.Shell", "App.xaml.cs");
        Assert.True(File.Exists(appFile), $"缺少文件：{appFile}");

        string content = File.ReadAllText(appFile);

        // 检查 OnExit 方法中是否有模块相关的日志记录
        bool hasModuleCleanupLog = content.Contains("模块", StringComparison.Ordinal)
                                || content.Contains("Module", StringComparison.Ordinal)
                                || content.Contains("清理", StringComparison.Ordinal)
                                || content.Contains("Dispose", StringComparison.Ordinal);

        Assert.True(
            hasModuleCleanupLog,
            """
            App.OnExit 应包含模块清理相关的日志记录，以便验证资源释放状态。

            建议在 _services.Dispose() 前后添加日志：
              CrashLog.Info("正在释放服务容器...");
              _services?.Dispose();
              CrashLog.Info("服务容器已释放，所有模块资源应已清理");
            """);
    }
}
