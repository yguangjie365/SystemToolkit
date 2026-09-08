using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 反模式守卫（2026-09-08 八轮 AI 审查沉淀：五类高频缺陷全部可静态扫描，机器拦 > 人查）。
/// <para>
/// 🔴 反模式 ①：NullLogger 空转——模块注册键控日志器，却用纯类型注册 VM，
/// VM 的 <c>ILogger? 可选参数</c>实际拿到 NullLogger（FileTransfer/FileBackup/GameManager/
/// NetManager/Settings 五处中招后统一修复，本守卫防回归）。
/// </para>
/// <para>
/// 🔴 反模式 ②：VM 层直接弹对话框/提示——对话框一律由 View 注入回调
/// （PickSavePath/PickOpenPath/ConfirmRequest/InfoRequest 模式）。
/// </para>
/// <para>
/// 🔴 反模式 ③：抓全局 Application.Current——后台事件必须构造注入 Dispatcher + RunOnUi
/// （同步 Invoke 死锁风险；Application 为 null 时静默跳过违反不静默红线）。
/// </para>
/// </summary>
public class RecurringDefectGuardTests
{
    private static string RepoRoot() => ViewLoadSmokeGuardTests.RepoRoot();

    /// <summary>模块目录下的 ViewModel/辅助类 .cs（排除 View code-behind、Module、obj/bin）。</summary>
    private static IEnumerable<string> EnumerateModuleVmFiles()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}SystemToolkit.Modules.", StringComparison.Ordinal)
                        && !p.EndsWith(".xaml.cs", StringComparison.Ordinal)
                        && !p.EndsWith("Module.cs", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> EnumerateModuleFiles()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}SystemToolkit.Modules.", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string ReadCode(string path)
    {
        // 剔除纯注释行（含 ///），避免「审查注释里提到反模式写法」被误伤——2026-09-08 实测：
        // FileBackup/FileTransfer/MusicManager 的修复注释里都引用了 Application.Current 写法
        string[] lines = File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToArray();
        return string.Join('\n', lines);
    }

    // ══════════ 反模式 ②：VM 层对话框 ══════════

    [Fact]
    public void Guard_VmFiles_MustNotOpenDialogsDirectly()
    {
        var violations = new List<string>();
        foreach (string file in EnumerateModuleVmFiles())
        {
            string code = ReadCode(file);
            foreach (string pattern in new[]                     {
                         "new Microsoft.Win32.OpenFileDialog",
                         "new Microsoft.Win32.SaveFileDialog",
                         "System.Windows.MessageBox.",
                     })
            {
                if (code.Contains(pattern, StringComparison.Ordinal))
                {
                    violations.Add($"{Relative(file)} 含「{pattern}」——对话框/提示必须由 View 注入回调" +
                                   "（PickSavePath/PickOpenPath/ConfirmRequest/InfoRequest 模式）");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "VM 层直弹对话框（反模式 ②，八轮审查 🔴 高频项）：\n" + string.Join("\n", violations));
    }

    // ══════════ 反模式 ③：全局 Application.Current ══════════

    /// <summary>
    /// VM/辅助类代码禁止抓全局 Application.Current——后台事件必须构造注入 Dispatcher + RunOnUi。
    /// 唯一合法接点：Module.cs 的 DI 工厂注册（<c>Application.Current?.Dispatcher</c> 在启动时
    /// UI 线程取一次注入）——本守卫只扫 VM 文件，Module.cs 豁免。
    /// </summary>
    [Fact]
    public void Guard_VmFiles_MustNotGrabGlobalApplication()
    {
        var violations = new List<string>();
        foreach (string file in EnumerateModuleVmFiles())
        {
            string code = ReadCode(file);
            if (code.Contains("Application.Current", StringComparison.Ordinal))
            {
                violations.Add($"{Relative(file)} 引用 Application.Current——后台事件必须构造注入 Dispatcher + RunOnUi" +
                               "（同步 Invoke 死锁 / null 静默跳过，八轮审查 🔴 高频项；唯一合法接点在 Module.cs 注册处）");
            }
        }

        Assert.True(violations.Count == 0,
            "VM 代码抓全局 Application.Current（反模式 ③）：\n" + string.Join("\n", violations));
    }

    // ══════════ 反模式 ①：键控日志器未接通（NullLogger 空转） ══════════

    private static readonly Regex KeyedLoggerRegistration =
        new(@"AddKeyedSingleton<ILogger>\(""(\w+)""", RegexOptions.Compiled);

    private static readonly Regex PureVmRegistration =
        new(@"AddSingleton<(\w+ViewModel)>\(\)", RegexOptions.Compiled);

    private static readonly Regex OptionalLoggerParam =
        new(@"ILogger\?\s*\w+\s*=\s*null", RegexOptions.Compiled);

    [Fact]
    public void Guard_KeyedLogger_MustBeWiredIntoVmFactory()
    {
        string srcRoot = Path.Combine(RepoRoot(), "src");
        var violations = new List<string>();

        foreach (string moduleDir in Directory.EnumerateDirectories(srcRoot, "SystemToolkit.Modules.*"))
        {
            string? moduleCs = EnumerateModuleFiles().FirstOrDefault(f =>
                Path.GetDirectoryName(f) == moduleDir && f.EndsWith("Module.cs", StringComparison.Ordinal));
            if (moduleCs is null || !File.ReadAllText(moduleCs).Contains("AddKeyedSingleton<ILogger>", StringComparison.Ordinal))
            {
                continue; // 未注册键控日志器的模块不适用本守卫
            }

            string code = File.ReadAllText(moduleCs);
            foreach (Match m in PureVmRegistration.Matches(code))
            {
                string vmName = m.Groups[1].Value;
                string vmFile = Path.Combine(moduleDir, vmName + ".cs");
                if (File.Exists(vmFile)
                    && OptionalLoggerParam.IsMatch(File.ReadAllText(vmFile)))
                {
                    violations.Add(
                        $"{Relative(moduleCs)}：{vmName} 纯类型注册，但其构造函数有可选 ILogger 参数——" +
                        "实际注入 NullLogger，键控日志器空转。改工厂注册并 GetRequiredKeyedService<ILogger>(...)（反模式 ①）");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "键控日志器未接通 VM（反模式 ①，八轮审查 🔴 高频项，五模块中招）：\n" + string.Join("\n", violations));
    }

    private static string Relative(string full) => Path.GetRelativePath(RepoRoot(), full).Replace('\\', '/');
}
