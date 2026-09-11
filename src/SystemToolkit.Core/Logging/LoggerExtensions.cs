using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// <see cref="ILogger"/> 的便捷方法：Trace / Debug / Fatal / 作用域。
/// <para>只做转发，不引入新语义——保持 ILogger 仍是「三个方法的最小契约」。</para>
/// </summary>
public static class LoggerExtensions
{
    /// <summary>记录 Trace（默认被级别过滤丢弃，<c>--diag</c> 才出）。</summary>
    public static void Trace(this ILogger logger, string message) =>
        logger.Log(LogLevel.Trace, message);

    /// <summary>记录 Debug（默认被级别过滤丢弃，<c>--diag</c> 才出）。</summary>
    public static void Debug(this ILogger logger, string message) =>
        logger.Log(LogLevel.Debug, message);

    /// <summary>记录 Fatal（进程即将终止或已无法继续）。</summary>
    public static void Fatal(this ILogger logger, string message, Exception? ex = null) =>
        logger.Log(LogLevel.Fatal, message, ex);

    /// <summary>
    /// 开启一个关联作用域：作用域内的日志自动带上同一个 cid，退出时恢复。
    /// 推荐包住「一次用户操作」的顶层（如一次备份、一次扫描）。
    /// </summary>
    public static IDisposable BeginScope(this ILogger logger, string? correlationId = null) =>
        LogScope.Begin(correlationId);

    /// <summary>
    /// 开启一次耗时测量（06 册 §4）：<c>using LogTiming t = logger.Time("BackupRule");</c>，
    /// 成功路径调 <c>t.Complete()</c>；未显式完成即释放按 <see cref="LogResult.Cancelled"/> 落
    /// Warn 记录——🔴 取消也要留痕，不许静默返回。
    /// 来源标签取 <see cref="ILogger.Source"/>；注入 <see cref="Contracts.NullLogger"/> 时整链静默。
    /// </summary>
    public static LogTiming Time(this ILogger logger, string action) =>
        new(logger, action);
}
