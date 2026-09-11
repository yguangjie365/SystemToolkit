using System.IO;
using SystemToolkit.Core.Logging;

namespace SystemToolkit.Shell;

/// <summary>
/// 启动期 / 全局异常兜底通道（06 分册 §8.3）：进程最早期（日志总线尚未装配）与崩溃临界点
/// 直写磁盘，不经缓冲；总线就绪后同时转发 <see cref="AppLog"/>（Serilog 落盘为 LOG-1 起）。
/// 禁止静默吞异常——闪退类问题没有日志就只能靠猜。
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>单文件滚动上限：超限归档为 .old（审查 2026-09-04 P2：run-*/firstchance-* 此前无上限）。</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>带 5MB 滚动的追加写（write 内部自行 lock(Gate) 外层已持锁）。</summary>
    private static void AppendWithRoll(string file, string contents)
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    // .old 被占用时退化为直接覆盖失败不致命——放弃本次归档，继续追加（审查：原实现 Move 抛异常会丢整条日志）
                    try
                    {
                        File.Move(file, file + ".old", overwrite: true);
                    }
                    catch (IOException)
                    {
                    }
                }

                File.AppendAllText(file, contents);
            }
            catch
            {
                // 日志通道自身失败时不能再抛（启动兜底路径）
            }
        }
    }

    /// <summary>
    /// 把记录投给 <see cref="AppLog"/> 总线（汇总 / 分模块 / JSONL 三处同时拿到一份）。
    /// <para>
    /// 🔴 自保优先级：总线自身的 <c>Write</c> 已 try/catch 各 sink，这里再兜一层——
    /// CrashLog 本身在「进程将死」路径上被调用，任何外泄都可能变成未处理异常。
    /// </para>
    /// </summary>
    private static void Forward(LogEntry entry)
    {
        try
        {
            AppLog.Write(entry);
        }
        catch
        {
            // 同 AppLog.Write 的兜底契约：日志通道自身炸了不能再抛
        }
    }

    public static void WriteFirstChance(Exception ex)
    {
        Forward(new LogEntry
        {
            Level = LogLevel.Warn,
            Source = "shell",
            Message = "[first-chance] " + ex.Message,
            Exception = ex,
        });

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemToolkit", "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"firstchance-{DateTime.Now:yyyyMMdd}.log");
            AppendWithRoll(file,
                $"[{DateTime.Now:HH:mm:ss.fff}] {ex.GetType().FullName}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n");
        }
        catch
        {
        }
    }

    public static void Info(string message)
    {
        Forward(new LogEntry { Level = LogLevel.Info, Source = "shell", Message = message });

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemToolkit", "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"run-{DateTime.Now:yyyyMMdd}.log");
            AppendWithRoll(file, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch
        {
        }
    }

    public static void Write(string source, Exception ex)
    {
        Forward(new LogEntry
        {
            Level = source.Contains("UnhandledException", StringComparison.Ordinal)
                    || source.Contains("熔断", StringComparison.Ordinal)
                ? LogLevel.Fatal
                : LogLevel.Error,
            Source = "shell",
            Message = source,
            Exception = ex,
        });

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemToolkit", "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");
            // 异常风暴防护：单日崩溃日志超 5MB 归档（2026-09-04 实测：容器渲染循环异常 4 分钟刷出 736MB）
            AppendWithRoll(file,
                $"[{DateTime.Now:HH:mm:ss.fff}] {source}\r\n{ex}\r\n\r\n");
        }
        catch
        {
            // 日志通道自身失败时不能再抛（启动兜底路径）
        }
    }
}
