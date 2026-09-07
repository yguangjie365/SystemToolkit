using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// 总线日志器：带固定来源标签，把 <see cref="ILogger"/> 的三次调用转成 <see cref="LogEntry"/> 投给
/// <see cref="AppLog"/>。
/// <para>
/// 与旧 <c>FileLogger</c> 的区别：旧实现自己管文件、自己拼格式、无法汇总；
/// 这里只负责「标注来源并投出」，落哪里由总线上的 sink 决定——所以同一条日志可以同时进
/// 分模块文件、汇总文件和 JSONL，而调用方无感。
/// </para>
/// </summary>
public sealed class BusLogger : ILogger
{
    /// <summary>来源标签（模块 id 或子系统名）。</summary>
    public string Source { get; }

    /// <summary>用给定来源构造。</summary>
    public BusLogger(string source) => Source = source;

    /// <inheritdoc/>
    public void Info(string message) => AppLog.Write(LogEntry.Create(LogLevel.Info, Source, message));

    /// <inheritdoc/>
    public void Warn(string message) => AppLog.Write(LogEntry.Create(LogLevel.Warn, Source, message));

    /// <inheritdoc/>
    public void Error(string message, Exception? ex = null) =>
        AppLog.Write(LogEntry.Create(LogLevel.Error, Source, message, ex));

    /// <inheritdoc/>
    public void Log(LogLevel level, string message, Exception? ex = null) =>
        AppLog.Write(LogEntry.Create(level, Source, message, ex));

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel level) => level >= AppLog.MinimumLevel;
}
