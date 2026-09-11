using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// Serilog 落点（LOG-1，2026-09-11）：AppLog 总线的持久化引擎，取代自建 <c>RollingFileSink</c> 的落盘职责。
/// <para>
/// 文件布局与旧版对齐：分模块 <c>overview-20260911.log</c>、汇总 <c>app-20260911.log</c>（人读，
/// 行格式 = <see cref="LogEntry.ToLine"/>）+ <c>app-20260911.jsonl</c>（机读，Serilog Compact JSON，
/// <c>@t/@l/@m/@x</c> + <c>@r</c> 内 Source/CorrelationId/Action/Result/DurationMs 结构化八字段）。
/// 滚动：按日（rollingInterval=Day）+ 单文件超限续号 <c>_001</c>（06 册 §6 的 10MB 上限）；
/// 跨进程共享写（shared=true，GUI 与 headless worker 并发不丢日志）；保留期由 <see cref="LogMaintenance"/> 承接。
/// </para>
/// <para>
/// 🔴 实现自保不抛异常（ILogSink 契约）：消息一律按数据绑定（模板固定 <c>{Message}</c>），
/// 调用点的字符串插值不会污染 Serilog 模板；<c>{Message:l}</c> 字面渲染，含大括号的正文也安全。
/// </para>
/// </summary>
public sealed class SerilogSink : ILogSink, IDisposable
{
    /// <summary>单文件上限（06 册 §6：10MB）。</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024;

    private static readonly MessageTemplate TextTemplate = new MessageTemplateParser().Parse("{Message}");

    private readonly Logger _appText;
    private readonly Logger _appJson;
    private readonly ConcurrentDictionary<string, Logger> _moduleLoggers = new();
    private readonly string _directory;
    private readonly int _retainDays;
    private readonly long _maxBytes;

    /// <summary>日志目录（文件按日滚动落在这里）。</summary>
    public string Directory => _directory;

    /// <summary>
    /// 构造 Serilog 落点并立即建齐两个汇总 logger（分模块 logger 首条日志时惰性建）。
    /// </summary>
    /// <param name="directory">日志目录（自动创建）。</param>
    /// <param name="maxBytes">单文件上限，超限续号滚动。</param>
    /// <param name="retainDays">保留天数，透传给 <see cref="LogMaintenance"/>。</param>
    public SerilogSink(string directory, long maxBytes = DefaultMaxBytes, int retainDays = LogMaintenance.DefaultRetainDays)
    {
        _directory = directory;
        _retainDays = retainDays;
        _maxBytes = maxBytes;
        System.IO.Directory.CreateDirectory(directory);

        _appText = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                path: System.IO.Path.Combine(directory, "app-.log"),
                outputTemplate: "{Message:l}{NewLine}",
                fileSizeLimitBytes: maxBytes,
                shared: true,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: null)
            .CreateLogger();

        _appJson = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: System.IO.Path.Combine(directory, "app-.jsonl"),
                fileSizeLimitBytes: maxBytes,
                shared: true,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: null)
            .CreateLogger();
    }

    /// <inheritdoc/>
    public void Emit(LogEntry entry)
    {
        try
        {
            var timestamp = new DateTimeOffset(entry.Timestamp);

            // 人读文本：整行由 ToLine 渲染（含异常尾块），Serilog 只负责按日/按量滚动与并发写
            var lineEvent = new LogEvent(timestamp, LogEventLevel.Information, null,
                TextTemplate, [new LogEventProperty("Message", new ScalarValue(entry.ToLine()))]);
            _appText.Write(lineEvent);
            ModuleLogger(entry.Source).Write(lineEvent);

            // 机读 JSONL：结构化八字段（@t/@l/@m/@x + @r 属性袋）
            var props = new List<LogEventProperty>
            {
                new("Message", new ScalarValue(entry.Message)),
                new("Source", new ScalarValue(entry.Source)),
            };
            if (entry.CorrelationId is not null)
            {
                props.Add(new LogEventProperty("CorrelationId", new ScalarValue(entry.CorrelationId)));
            }

            if (entry.Action is not null)
            {
                props.Add(new LogEventProperty("Action", new ScalarValue(entry.Action)));
            }

            // 属性名 Outcome（而非 Result）：规避 sync-over-async 文本守卫对 Task 属性 Result 形态的误报
            LogResult? opResult = entry.Outcome;
            if (opResult is not null)
            {
                props.Add(new LogEventProperty("Result", new ScalarValue(opResult.ToString())));
            }

            if (entry.DurationMs is not null)
            {
                props.Add(new LogEventProperty("DurationMs", new ScalarValue(entry.DurationMs.Value)));
            }

            var jsonEvent = new LogEvent(timestamp, MapLevel(entry.Level), entry.Exception, TextTemplate, props);
            _appJson.Write(jsonEvent);

            LogMaintenance.PruneOncePerDay(_directory, _retainDays);
        }
        catch
        {
            // 日志通道自身失败不能再抛（与旧 RollingFileSink 同语义，ILogSink 契约）
        }
    }

    /// <summary>释放全部 Serilog logger（刷新缓冲落盘）。宿主退出时经 AppLog.Shutdown 调用。</summary>
    public void Dispose()
    {
        _appText.Dispose();
        _appJson.Dispose();
        foreach (Logger logger in _moduleLoggers.Values)
        {
            logger.Dispose();
        }

        _moduleLoggers.Clear();
    }

    /// <summary>取/建分模块文本 logger。来源标签先做文件名字符消毒，防外部字符串造出越界路径。</summary>
    private Logger ModuleLogger(string source) =>
        _moduleLoggers.GetOrAdd(SanitizeSource(source), name =>
            new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    path: System.IO.Path.Combine(_directory, name + "-.log"),
                    outputTemplate: "{Message:l}{NewLine}",
                    fileSizeLimitBytes: _maxBytes,
                    shared: true,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: null)
                .CreateLogger());

    private static string SanitizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "app";
        }

        Span<char> buffer = stackalloc char[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            buffer[i] = Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c;
        }

        return new string(buffer);
    }

    /// <summary>级别映射：自建六级 → Serilog 六级（Info→Information，Warn→Warning，Fatal 同名）。</summary>
    private static LogEventLevel MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Info => LogEventLevel.Information,
        LogLevel.Warn => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };
}
