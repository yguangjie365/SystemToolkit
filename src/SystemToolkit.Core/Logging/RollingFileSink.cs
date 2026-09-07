using System.Globalization;
using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// 滚动文件落点：按天分文件、单文件超上限归档 .old、按保留期清理过期文件。
/// <para>
/// 同一个实现同时支撑两种用法，靠 <c>nameSelector</c> 区分：
/// <list type="bullet">
/// <item>分模块：<c>e => e.Source</c> → <c>overview-20260906.log</c>（沿用既有习惯）</item>
/// <item>汇总：<c>_ =&gt; "app"</c> → <c>app-20260906.log</c> + <c>app-20260906.jsonl</c></item>
/// </list>
/// </para>
/// <para>
/// 🔴 为什么必须做滚动与保留期：旧工程出过 4 分钟刷出 736MB 日志的事故。
/// 日志是排查资产，但无上限的日志自己就是故障源——量的上限和保留期是同一件事的两面。
/// </para>
/// </summary>
public sealed class RollingFileSink : ILogSink
{
    /// <summary>默认单文件上限（20MB；与 CrashLog 的 5MB 不同，因为汇总流包含全部模块）。</summary>
    public const long DefaultMaxBytes = 20L * 1024 * 1024;

    /// <summary>默认保留天数。</summary>
    public const int DefaultRetainDays = 14;

    private static readonly object StaticGate = new();
    private static DateTime _lastPruneDate = DateTime.MinValue;

    private readonly object _gate = new();
    private readonly Func<LogEntry, string> _nameSelector;
    private readonly bool _writeJsonl;

    /// <summary>日志目录。</summary>
    public string Directory { get; }

    /// <summary>单文件上限（字节），超限归档为 .old。</summary>
    public long MaxBytes { get; }

    /// <summary>保留天数，超期的 .log/.jsonl/.old 一并删除。</summary>
    public int RetainDays { get; }

    /// <summary>构造一个滚动文件落点。</summary>
    /// <param name="directory">日志目录（会自动创建）。</param>
    /// <param name="nameSelector">由记录决定文件名前缀。</param>
    /// <param name="writeJsonl">是否额外写一份 JSONL（机读，供检索/分析）。</param>
    /// <param name="maxBytes">单文件上限。</param>
    /// <param name="retainDays">保留天数。</param>
    public RollingFileSink(
        string directory,
        Func<LogEntry, string> nameSelector,
        bool writeJsonl = false,
        long maxBytes = DefaultMaxBytes,
        int retainDays = DefaultRetainDays)
    {
        Directory = directory;
        _nameSelector = nameSelector;
        _writeJsonl = writeJsonl;
        MaxBytes = maxBytes;
        RetainDays = retainDays;
    }

    /// <inheritdoc/>
    public void Emit(LogEntry entry)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string name = _nameSelector(entry);
            string date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            AppendWithRoll(Path.Combine(Directory, $"{name}-{date}.log"), entry.ToLine());
            if (_writeJsonl)
            {
                AppendWithRoll(Path.Combine(Directory, $"{name}-{date}.jsonl"), entry.ToJson());
            }

            PruneOncePerDay();
        }
        catch
        {
            // 日志通道自身失败时不能再抛（写入失败无法补救，抛出去只会制造次生崩溃）
        }
    }

    /// <summary>带滚动的追加写（.old 被占用时放弃归档继续追加，不能因归档失败丢整条日志）。</summary>
    private void AppendWithRoll(string file, string content)
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    try
                    {
                        File.Move(file, file + ".old", overwrite: true);
                    }
                    catch (IOException)
                    {
                        // 归档失败不致命：继续追加，宁可文件偏大也不能丢日志
                    }
                }

                File.AppendAllText(file, content + "\r\n");
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 清理过期日志。按天名里的日期判定（而非文件修改时间——.old 的修改时间不可靠），
    /// 且每个进程每天只做一次，避免每条日志都触发一次目录枚举。
    /// </summary>
    private void PruneOncePerDay()
    {
        DateTime today = DateTime.Today;
        lock (StaticGate)
        {
            if (_lastPruneDate == today)
            {
                return;
            }

            _lastPruneDate = today;
            DateTime cutoff = today.AddDays(-RetainDays);

            try
            {
                foreach (string file in System.IO.Directory.GetFiles(Directory))
                {
                    Match m = Regex.Match(Path.GetFileName(file), @"-(\d{8})\.(log|jsonl)(\.old)?$");
                    if (!m.Success)
                    {
                        continue;
                    }

                    if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDate))
                    {
                        continue;
                    }

                    if (fileDate < cutoff)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // 单个文件删除失败（被占用/只读）不影响其余清理
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>测试用：重置「每天只清一次」的记忆，让保留期逻辑可被反复验证。</summary>
    internal static void ResetPruneMemory()
    {
        lock (StaticGate)
        {
            _lastPruneDate = DateTime.MinValue;
        }
    }
}
