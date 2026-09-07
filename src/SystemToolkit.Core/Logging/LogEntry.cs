using System.Text.Json;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// 一条日志记录。进程内传递的最小完整单元：谁（Source）、何时、多严重、说什么、异常与关联 ID。
/// </summary>
public sealed record LogEntry
{
    /// <summary>本地时间戳（与既有 FileLogger / CrashLog 保持一致，便于人眼对齐）。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>级别。</summary>
    public LogLevel Level { get; init; } = LogLevel.Info;

    /// <summary>来源标签：模块 id（overview / appmanager…）或子系统名（shell / elevatedhelper）。</summary>
    public string Source { get; init; } = "app";

    /// <summary>消息正文。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>异常（可为 null）。</summary>
    public Exception? Exception { get; init; }

    /// <summary>关联 ID：同一次用户操作跨模块产生的日志共享一个，便于串联。</summary>
    public string? CorrelationId { get; init; }

    /// <summary>构造一条记录（自动补时间与当前关联 ID）。</summary>
    public static LogEntry Create(LogLevel level, string source, string message,
        Exception? ex = null, string? correlationId = null) =>
        new()
        {
            Level = level,
            Source = source,
            Message = message,
            Exception = ex,
            CorrelationId = correlationId ?? LogScope.CorrelationId,
        };

    /// <summary>渲染为单行文本（人读）。</summary>
    public string ToLine()
    {
        string cid = CorrelationId is null ? string.Empty : $" [{CorrelationId}]";
        string head = $"[{Timestamp:HH:mm:ss.fff}] [{Level.ToString().ToUpperInvariant()}] [{Source}]{cid} {Message}";
        return Exception is null ? head : head + "\r\n" + Exception;
    }

    /// <summary>
    /// 渲染为单行 JSON（机读，JSONL）。
    /// <para>异常只取类型/消息/栈三个字段，不做完整序列化——Exception 对象图含 TargetSite 等，
    /// 直接 <see cref="JsonSerializer"/> 会抛或产生巨量噪音。</para>
    /// </summary>
    public string ToJson()
    {
        var dto = new
        {
            ts = Timestamp.ToString("o"),
            level = Level.ToString(),
            source = Source,
            cid = CorrelationId,
            message = Message,
            exType = Exception?.GetType().FullName,
            exMessage = Exception?.Message,
            stack = Exception?.StackTrace,
        };

        return JsonSerializer.Serialize(dto);
    }
}
