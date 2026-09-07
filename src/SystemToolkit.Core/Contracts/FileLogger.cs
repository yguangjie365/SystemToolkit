using SystemToolkit.Core.Logging;

namespace SystemToolkit.Core.Contracts;

/// <summary>
/// 带来源标签的文件日志器（<c>{prefix}-yyyyMMdd.log</c>）。
/// <para>
/// 2026-09-06 改造：本类不再自己管文件，改为把记录投给 <see cref="AppLog"/> 总线，
/// 由总线上的 sink 决定落点——于是同一条日志能同时进「分模块文件 + 汇总文件 + JSONL」，
/// 而 7 个模块的既有注册代码（<c>new FileLogger("appmanager")</c>）一行都不用改。
/// </para>
/// <para>
/// ⚠️ 总线未装落点时（单元测试场景）本类不写盘——这是刻意的：
/// 测试不该往用户 AppData 里写东西。宿主启动会调 <see cref="AppLog.UseDefaultFileSinks"/>。
/// </para>
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly BusLogger _inner;

    /// <summary>日志文件名前缀（同时作为日志来源标签）。</summary>
    public string Prefix { get; }

    /// <summary>按前缀命名（prefix 区分模块来源）。</summary>
    public FileLogger(string prefix = "app")
    {
        Prefix = prefix;
        _inner = new BusLogger(prefix);
    }

    /// <inheritdoc/>
    public void Info(string message) => _inner.Info(message);

    /// <inheritdoc/>
    public void Warn(string message) => _inner.Warn(message);

    /// <inheritdoc/>
    public void Error(string message, Exception? ex = null) => _inner.Error(message, ex);

    /// <inheritdoc/>
    public void Log(LogLevel level, string message, Exception? ex = null) => _inner.Log(level, message, ex);

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel level) => _inner.IsEnabled(level);
}
