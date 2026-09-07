using System.Text.RegularExpressions;

namespace SystemToolkit.Tests;

/// <summary>
/// 异步服务的同步释放契约守卫（2026-09-06 崩溃事故防回归）。
/// <para>
/// <b>事故</b>：宿主 <c>App.OnExit</c> 调 <c>ServiceProvider.Dispose()</c>（同步）。
/// 若某个单例只实现 <see cref="IAsyncDisposable"/>、没实现 <see cref="IDisposable"/>，
/// MS DI 会抛 <c>InvalidOperationException: type only implements IAsyncDisposable</c>。
/// 该异常发生在退出路径 → 未处理 → 「关闭程序即崩溃」（退出码 0xE0434352），
/// 且崩溃日志为空（CrashLog 处理器此时已被摘除），极难定位。
/// </para>
/// <para>
/// <b>约束</b>：任何实现「本身继承 IAsyncDisposable 的项目内接口」的具体类型，
/// 必须<b>同时</b>声明 <see cref="IDisposable"/>。只写一个 <c>public void Dispose()</c>
/// 方法是不够的——类型必须真的实现接口，否则 <c>instance is IDisposable</c> 为 false，DI 照样抛。
/// </para>
/// <para>
/// 范围说明：只检查<b>项目内声明的、继承 IAsyncDisposable 的接口</b>的实现类。
/// 直接实现 <see cref="IAsyncDisposable"/> 的局部租约类（如 <c>VssLease</c>，用 <c>await using</c>）
/// 不在范围内——它们不进 DI 容器，加了 IDisposable 反而会诱导误用 <c>using</c>。
/// </para>
/// </summary>
public class AsyncDisposableServiceGuardTests
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
    public void Service_ImplementingProjectAsyncDisposableInterface_MustAlsoDeclareIDisposable()
    {
        string[] sources = Directory.GetFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();

        // ① 找出项目内「继承 IAsyncDisposable 的接口」
        var asyncInterfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in sources)
        {
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(file),
                         @"interface\s+(I\w+)\s*:[^{]*\bIAsyncDisposable\b"))
            {
                asyncInterfaces.Add(m.Groups[1].Value);
            }
        }

        Assert.NotEmpty(asyncInterfaces); // 反向验证：一个都没扫到说明正则失效，守卫形同虚设

        // ② 找出实现了这些接口、却没声明 IDisposable 的具体类
        var offenders = new List<string>();
        foreach (string file in sources)
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"class\s+(\w+)\s*:\s*([^{\r\n]+)"))
            {
                string className = m.Groups[1].Value;
                string baseList = m.Groups[2].Value;

                bool implementsAsyncService = asyncInterfaces.Any(i =>
                    Regex.IsMatch(baseList, $@"\b{i}\b"));
                if (!implementsAsyncService)
                {
                    continue;
                }

                if (!Regex.IsMatch(baseList, @"\bIDisposable\b"))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}：{className}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下类型实现了「继承 IAsyncDisposable 的接口」却未声明 IDisposable —— " +
            "宿主同步 ServiceProvider.Dispose() 会抛 InvalidOperationException，表现为「关闭程序即崩溃」："
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Select(o => "  · " + o))
            + Environment.NewLine + "修法：类声明加 IDisposable，并实现同步 Dispose 方法（有界等待 DisposeAsync，"
            + "勿直接同步阻塞等待异步停止——退出时可能是 UI 线程，会死锁）。"
            + Environment.NewLine + "注意：只写方法不够，类型必须真的声明 IDisposable，否则 is IDisposable 为 false，DI 照样抛。");
    }
}
