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

    /// <summary>动作名（PascalCase 方法语义，06 册 §2 八字段之一；可选，渐进补齐）。</summary>
    public string? Action { get; init; }

    /// <summary>操作结果（长耗时/破坏性操作必填；瞬时操作可空）。
    /// 命名 Outcome 而非 Result：外部成员访问写法会被 sync-over-async 源码守卫误报（01 §4.3），
    /// 序列化键名保持 <c>result</c>，与 06 册 §2 八字段对齐。</summary>
    public LogResult? Outcome { get; init; }

    /// <summary>耗时毫秒数（06 册 §2：预期 &gt;500ms 的操作必填）。</summary>
    public long? DurationMs { get; init; }

    /// <summary>构造一条记录（自动补时间与当前关联 ID）。</summary>
    public static LogEntry Create(LogLevel level, string source, string message,
        Exception? ex = null, string? correlationId = null,
        string? action = null, LogResult? outcome = null, long? durationMs = null) =>
        new()
        {
            Level = level,
            Source = source,
            Message = message,
            Exception = ex,
            CorrelationId = correlationId ?? LogScope.CorrelationId,
            Action = action,
            Outcome = outcome,
            DurationMs = durationMs,
        };

    /// <summary>渲染为单行文本（人读）。</summary>
    public string ToLine()
    {
        string cid = CorrelationId is null ? string.Empty : $" [{CorrelationId}]";
        string act = Action is null ? string.Empty : $" [{Action}]";
        string tail = Outcome is null
            ? DurationMs is null ? string.Empty : $" in {DurationMs} ms"
            : $" → {Outcome}" + (DurationMs is null ? string.Empty : $" in {DurationMs} ms");
        string head = $"[{Timestamp:HH:mm:ss.fff}] [{Level.ToString().ToUpperInvariant()}] [{Source}]{cid}{act} {Message}{tail}";
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
            act = Action,
            result = Outcome?.ToString(),
            dur = DurationMs,
            message = Message,
            exType = Exception?.GetType().FullName,
            exMessage = Exception?.Message,
            stack = Exception?.StackTrace,
        };

        return JsonSerializer.Serialize(dto);
    }
}
