using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 源码健全性守卫（M12 补齐：03 分册宣称的 AsyncGuard / AtomicFileGuard）。
/// 检测逻辑抽为纯函数并内建「样本注入=红 / 干净样本=绿」反向验证（03 §4.1：未经反向验证的守门等于没守门）。
/// </summary>
public class SourceSanityGuardTests
{
    /// <summary>
    /// 2026-09-08 修正：原正则 <c>\.Wait\(\)</c> 只匹配<b>无参</b>调用，
    /// <c>.Wait(TimeSpan.FromSeconds(2))</c> 这类<b>带参</b> sync-over-async 全部漏网
    /// （实测 DeviceDiscoveryService/FileTransferService/FileWebServer 三处 .Wait(2s) 未被拦截）。
    /// 改为 <c>\.\s*Wait\s*\(</c> 覆盖全部重载。
    /// </summary>
    private const string AsyncPattern = @"\.\s*Result\b|\.\s*Wait\s*\(|GetAwaiter\s*\(\)\s*\.\s*GetResult\s*\(\)";
    private const string SyncFilePattern =
        @"(?<![\w])File\.(WriteAllText|WriteAllTextAsync|WriteAllLines|WriteAllLinesAsync|AppendAllText|AppendAllLines)\(";

    /// <summary>日志追加 / 崩溃转储 / Helper 结果文件（写失败无法补救）/ 用户另存导出 / AtomicFile 实现本体 /
    /// HardwareSensorProbe（LHM Open 的 15s 有界兜底，审查 P1-8 定性，红线豁免待批注）。</summary>
    private static readonly string[] AllowedFiles =
    {
        "AtomicFile.cs",
        "FileLogger.cs",
        "CrashLog.cs",
        "Program.cs", // ElevatedHelper
        "VssRunner.cs", // ElevatedHelper VSS verb：结果文件与 Program.cs 同语义（写失败无法补救，退出码即结果）
        "OsVerRunner.cs", // ElevatedHelper osver verb：一次性 %TEMP% 结果文件，同上语义（调用方读后即删）
        "OverviewViewModel.Report.cs",
        "HardwareSensorProbe.cs",
        // 2026-09-11（LOG-1）：RollingFileSink.cs 已删除——日志落盘移交 Serilog（SerilogSink 无直写模式），
        // LogMaintenance 仅 File.Delete（不在本守卫扫描面内），豁免随之移除。
        // 2026-09-08（正则修正后暴露）：Dispose 路径的 Task.Wait(2s) 有界兜底——
        // IDisposable 无法 await，2s 上界后放弃等待属既定取舍，与 FileTransferService 同语义。
        "DeviceDiscoveryService.cs",
        "FileTransferService.cs",
        "FileWebServer.cs",
    };

    [Theory]
    [InlineData("var x = task.Result;", true)]
    [InlineData("task.Wait();", true)]
    [InlineData("task.GetAwaiter().GetResult();", true)]
    [InlineData("task.GetAwaiter () . GetResult();", true)]
    [InlineData("await task.ConfigureAwait(false);", false)]
    [InlineData("task.WaitAsync(ts);", false)]
    [InlineData("proc.WaitForExit();", false)]
    public void AsyncGuard_DetectorFunction_SampleInjection_ReverseVerification(string sample, bool expectViolation)
    {
        List<string> hits = FindAsyncViolations(sample);
        Assert.Equal(expectViolation, hits.Count > 0);
    }

    [Theory]
    [InlineData("File.WriteAllText(path, text);", true)]
    [InlineData("File.WriteAllTextAsync(path, text);", true)]
    [InlineData("File.AppendAllText(path, text);", true)]
    [InlineData("AtomicFile.WriteAllText(path, text);", false)]
    [InlineData("File.ReadAllText(path);", false)]
    [InlineData("File.Copy(a, b);", false)]
    public void AtomicFileGuard_DetectorFunction_SampleInjection_ReverseVerification(string sample, bool expectViolation)
    {
        List<string> hits = FindSyncFileWrites(sample);
        Assert.Equal(expectViolation, hits.Count > 0);
    }

    [Fact]
    public void AsyncGuard_WholeRepoSource_ZeroViolations()
    {
        List<string> offenders = SourceSanityGuard.ScanRepo(RepoRoot(), FindAsyncViolations, AllowedFiles);
        Assert.True(offenders.Count == 0,
            "发现 sync-over-async（01 §4.3 🔴：.Result/.Wait()/GetAwaiter().GetResult()）——" +
            "如属有界兜底请移入豁免名单并注明依据：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void AtomicFileGuard_WholeRepoSource_ZeroViolations()
    {
        List<string> offenders = SourceSanityGuard.ScanRepo(RepoRoot(), FindSyncFileWrites, AllowedFiles);
        Assert.True(offenders.Count == 0,
            "发现绕过 AtomicFile 的落盘写入（02 §六：中断会产生半截文件）——" +
            "如属日志追加/用户另存等合理场景请移入豁免名单并注明依据：\n" + string.Join("\n", offenders));
    }

    private static List<string> FindAsyncViolations(string text) =>
        ScanLines(text, AsyncPattern);

    private static List<string> FindSyncFileWrites(string text) =>
        ScanLines(text, SyncFilePattern);

    private static List<string> ScanLines(string text, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.Compiled);
        var hits = new List<string>();
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (regex.IsMatch(lines[i]))
            {
                hits.Add($"行{i + 1}: {lines[i].Trim()}");
            }
        }

        return hits;
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

    /// <summary>扫描函数与仓库遍历集中于此（internal 供反向验证直测）。</summary>
    internal static class SourceSanityGuard
    {
        public static List<string> ScanRepo(
            string repoRoot, Func<string, List<string>> scan, IReadOnlyList<string> allowedFiles)
        {
            var offenders = new List<string>();
            foreach (string file in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/obj/") || normalized.Contains("/bin/"))
                {
                    continue;
                }

                string name = Path.GetFileName(file);
                if (allowedFiles.Any(a => name == a))
                {
                    continue;
                }

                foreach (string hit in scan(File.ReadAllText(file)))
                {
                    offenders.Add($"{normalized.Replace(repoRoot, "")} {hit}");
                }
            }

            return offenders;
        }
    }
}
