using System.Globalization;
using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// 日志保留期维护（LOG-1 起从自建 RollingFileSink 独立出来）：按文件名日期判定过期删除 +
/// 总量封顶（06 册 §6：默认 14 天 / 超 200MB 从最旧删起）。
/// <para>
/// 认三种名字：Serilog 按日产物 <c>name-20260911.log</c>、按量续号 <c>name-20260911_001.log</c>、
/// 以及旧 RollingFileSink 时代的 <c>name-20260911.log.old</c> 归档（存量文件也要能被清掉）。
/// 每进程每天只做一次，避免每条日志触发目录枚举；测试用 <see cref="ResetPruneMemory"/> 复位。
/// </para>
/// </summary>
public static class LogMaintenance
{
    /// <summary>默认保留天数。</summary>
    public const int DefaultRetainDays = 14;

    /// <summary>logs\ 目录总量上限（超限从最旧文件删起）。</summary>
    public const long DefaultTotalCapBytes = 200L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Regex DateName = new(
        @"-(\d{8})(?:_\d{3})?\.(?:log|jsonl)(?:\.old)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static DateTime _lastPruneDate = DateTime.MinValue;

    /// <summary>每天一次的保留期清理（按文件名日期，不用 LastWriteTime——.old 的修改时间不可靠）。</summary>
    public static void PruneOncePerDay(string directory,
        int retainDays = DefaultRetainDays, long maxTotalBytes = DefaultTotalCapBytes)
    {
        DateTime today = DateTime.Today;
        lock (Gate)
        {
            if (_lastPruneDate == today)
            {
                return;
            }

            _lastPruneDate = today;
        }

        PruneCore(directory, today, retainDays, maxTotalBytes);
    }

    /// <summary>立即执行一轮清理（无视「每天一次」记忆；启动期与测试用）。</summary>
    public static void Prune(string directory,
        int retainDays = DefaultRetainDays, long maxTotalBytes = DefaultTotalCapBytes)
    {
        PruneCore(directory, DateTime.Today, retainDays, maxTotalBytes);
    }

    /// <summary>测试用：复位「每天只清一次」的记忆。</summary>
    internal static void ResetPruneMemory()
    {
        lock (Gate)
        {
            _lastPruneDate = DateTime.MinValue;
        }
    }

    private static void PruneCore(string directory, DateTime today, int retainDays, long maxTotalBytes)
    {
        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return;
            }

            DateTime cutoff = today.AddDays(-retainDays);
            var dated = new List<(DateTime Date, string File, long Size)>();

            foreach (string file in System.IO.Directory.GetFiles(directory))
            {
                Match m = DateName.Match(Path.GetFileName(file));
                if (!m.Success
                    || !DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDate))
                {
                    continue;
                }

                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                if (fileDate < cutoff)
                {
                    TryDelete(file);
                }
                else
                {
                    dated.Add((fileDate, file, size));
                }
            }

            // 总量封顶：从最旧往新删，压回上限之内（同日多文件按文件名次序）
            long total = 0;
            foreach ((_, _, long size) in dated)
            {
                total += size;
            }

            dated.Sort((a, b) => DateTime.Compare(a.Date, b.Date));
            foreach ((_, string file, long size) in dated)
            {
                if (total <= maxTotalBytes)
                {
                    break;
                }

                TryDelete(file);
                total -= size;
            }
        }
        catch
        {
            // 清理失败不影响日志通道本身
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // 被占用/只读的文件跳过，下轮再清
        }
    }
}
