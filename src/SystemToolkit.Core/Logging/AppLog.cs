using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// 进程级日志总线：所有 <see cref="ILogger"/> 的汇聚点，一条记录分发给所有已注册的 <see cref="ILogSink"/>。
/// <para>
/// 为什么需要它：既有实现是「每个模块 new 一个 FileLogger 各写各的文件」，
/// 排查一次跨模块问题要在 overview / appmanager / crash / firstchance 好几个文件之间翻——
/// 本次「关闭即崩溃」就是靠碰巧开着 --diag 才定位到的。总线让「一份按时间归并的汇总」成为可能。
/// </para>
/// <para>
/// <b>默认不装任何落点</b>：单元测试里日志不落盘（不污染用户 AppData）；
/// 宿主启动时调 <see cref="UseDefaultFileSinks"/> 装上文件落点。
/// </para>
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly List<ILogSink> Sinks = new();
    private static LogLevel _minimumLevel = LogLevel.Info;

    /// <summary>日志目录（默认 %LOCALAPPDATA%\SystemToolkit\logs，与既有 CrashLog / FileLogger 同目录）。</summary>
    public static string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", "logs");

    /// <summary>当前最低级别（低于此级别的记录直接丢弃，不进落点）。</summary>
    public static LogLevel MinimumLevel
    {
        get
        {
            lock (Gate)
            {
                return _minimumLevel;
            }
        }

        set
        {
            lock (Gate)
            {
                _minimumLevel = value;
            }
        }
    }

    /// <summary>注册一个落点。重复注册同一个实例不生效。</summary>
    public static void AddSink(ILogSink sink)
    {
        lock (Gate)
        {
            if (!Sinks.Contains(sink))
            {
                Sinks.Add(sink);
            }
        }
    }

    /// <summary>
    /// 装上默认文件落点：<see cref="SerilogSink"/>（分模块文本 + 汇总文本/JSONL，
    /// 按日 + 单文件超限双滚动，保留期与 200MB 总量封顶由 <see cref="LogMaintenance"/> 承接）。
    /// 宿主启动时调用一次即可。
    /// </summary>
    /// <param name="directory">覆盖默认目录（测试用）。</param>
    /// <param name="retainDays">保留天数。</param>
    /// <param name="maxBytes">单文件上限（06 册 §6：10MB）。</param>
    public static void UseDefaultFileSinks(
        string? directory = null,
        int retainDays = LogMaintenance.DefaultRetainDays,
        long maxBytes = SerilogSink.DefaultMaxBytes)
    {
        AddSink(new SerilogSink(directory ?? LogDirectory, maxBytes, retainDays));
    }

    /// <summary>清空全部落点并把级别复位（测试用，避免用例间互相污染）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Sinks.Clear();
            _minimumLevel = LogLevel.Info;
        }
    }

    /// <summary>
    /// 退出前冲刷并释放可销毁的落点（Serilog 缓冲刷新）。宿主 OnExit 调用，
    /// 调用后总线回到「无落点」状态，与 <see cref="Reset"/> 的区别是会先 Dispose。
    /// </summary>
    public static void Shutdown()
    {
        ILogSink[] snapshot;
        lock (Gate)
        {
            snapshot = Sinks.ToArray();
            Sinks.Clear();
        }

        foreach (ILogSink sink in snapshot)
        {
            if (sink is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // 关闭通道的失败不再外泄（同 Write 的兜底契约）
                }
            }
        }
    }

    /// <summary>
    /// 写一条记录：先过级别过滤，再分发。
    /// <para>单个落点抛异常不影响其它落点，也不外泄——日志通道的失败绝不能变成业务失败。</para>
    /// </summary>
    public static void Write(LogEntry entry)
    {
        if (entry.Level < MinimumLevel)
        {
            return;
        }

        ILogSink[] snapshot;
        lock (Gate)
        {
            if (Sinks.Count == 0)
            {
                return;
            }

            snapshot = Sinks.ToArray();
        }

        foreach (ILogSink sink in snapshot)
        {
            try
            {
                sink.Emit(entry);
            }
            catch
            {
                // 落点自身炸了不影响别人，也不外泄
            }
        }
    }

    /// <summary>创建一个带来源标签的日志器（模块注册时用）。</summary>
    public static ILogger CreateLogger(string source) => new BusLogger(source);
}
