using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Shell;

/// <summary>
/// 诊断包导出：把日志 + 环境信息打包成一个 zip，供用户直接发给开发者。
/// <para>
/// 为什么需要：排查问题的标准流程是「让用户找日志 → 用户不知道在哪 → 来回三轮」。
/// 一键导出把这个过程压成一步，用户只需要把桌面上的 zip 发出来。
/// </para>
/// <para>
/// 🔴 脱敏约束：只收日志与环境信息（版本/系统/.NET/命令行），
/// <b>不收配置文件、不收备份规则里的具体路径、不收任何凭据</b>——
/// 日志本身可能含路径，导出前在 UI/命令行提示中说明，由用户自行确认。
/// </para>
/// </summary>
public static class DiagnosticsExporter
{
    /// <summary>
    /// 打包诊断信息，返回 zip 路径。
    /// </summary>
    /// <param name="outputDirectory">输出目录（默认桌面）。</param>
    public static string Export(string? outputDirectory = null)
    {
        string dir = outputDirectory
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                         "SystemToolkit");

        System.IO.Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, $"SystemToolkit-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        string staging = Path.Combine(Path.GetTempPath(), "SystemToolkit-diag-" + Guid.NewGuid().ToString("n")[..8]);
        System.IO.Directory.CreateDirectory(staging);

        try
        {
            // ① 环境信息（无敏感数据）
            AtomicFile.WriteAllText(Path.Combine(staging, "environment.txt"), BuildEnvironmentReport());

            // ② 全部日志（分模块 + 汇总 + 崩溃 + 首次异常 + 运行日志）
            string logDir = AppLog.LogDirectory;
            if (System.IO.Directory.Exists(logDir))
            {
                string logsStage = Path.Combine(staging, "logs");
                System.IO.Directory.CreateDirectory(logsStage);

                foreach (string file in System.IO.Directory.GetFiles(logDir))
                {
                    // 只收 .log / .jsonl / .old，跳过目录与无关文件
                    string ext = Path.GetExtension(file);
                    if (ext is ".log" or ".jsonl" or ".old")
                    {
                        File.Copy(file, Path.Combine(logsStage, Path.GetFileName(file)), overwrite: true);
                    }
                }
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响导出结果（下次导出用新的 GUID 目录）
            }
        }
    }

    /// <summary>生成环境报告文本（供人工核对版本与运行环境）。</summary>
    public static string BuildEnvironmentReport()
    {
        var lines = new List<string>
        {
            $"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"应用版本: {ThisVersion()}",
            $"进程: {(Environment.Is64BitProcess ? "x64" : "x86")}",
            $"操作系统: {RuntimeInformation.OSDescription}",
            $"系统架构: {RuntimeInformation.OSArchitecture}",
            $".NET 运行时: {Environment.Version}",
            $"命令行: {Environment.CommandLine}",
            $"日志目录: {AppLog.LogDirectory}",
            $"当前最低日志级别: {AppLog.MinimumLevel}",
            $"本机时区: {TimeZoneInfo.Local.DisplayName}",
            string.Empty,
            "— 说明 —",
            "本包仅包含日志文件与环境信息，不含配置文件与凭据。",
            "日志中可能包含文件路径；如介意请自行编辑后再发送。",
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string ThisVersion()
    {
        try
        {
            System.Reflection.Assembly asm = typeof(DiagnosticsExporter).Assembly;
            string? informational = asm
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            return string.IsNullOrWhiteSpace(informational) ? asm.GetName().Version?.ToString() ?? "unknown" : informational;
        }
        catch
        {
            return "unknown";
        }
    }
}
