using System.Diagnostics;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// 耗时测量协议（<see cref="LoggerExtensions.Time"/> 的返回体）：
/// 一条记录带齐 Action / Result / Duration 三字段（06 册 §2 八字段中的动态三件套）。
/// <para>
/// 🔴 未显式 <see cref="Complete"/> 即释放 = 提前 return/异常路径，按
/// <see cref="LogResult.Cancelled"/> 落 Warn 记录——取消也必须留痕（06 册 §3）。
/// </para>
/// </summary>
public sealed class LogTiming : IDisposable
{
    private readonly string _source;
    private readonly string _action;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private bool _completed;

    /// <summary>由 <see cref="LoggerExtensions.Time"/> 创建，勿直接 new。</summary>
    internal LogTiming(ILogger logger, string source, string action)
    {
        Logger = logger;
        _source = source;
        _action = action;
    }

    private ILogger Logger { get; }

    /// <summary>显式落一条完成记录（默认 Success；失败/拒绝路径传对应值与级别）。幂等：只落第一条。</summary>
    public void Complete(
        LogResult outcome = LogResult.Success,
        LogLevel level = LogLevel.Info,
        string? message = null,
        Exception? ex = null)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        AppLog.Write(LogEntry.Create(
            level, _source,
            message ?? $"{_action} {(outcome == LogResult.Success ? "完成" : outcome.ToString())}",
            ex, action: _action, outcome: outcome, durationMs: _stopwatch.ElapsedMilliseconds));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_completed)
        {
            return;
        }

        Complete(LogResult.Cancelled, LogLevel.Warn, $"{_action} 未走完（作用域退出，按取消留痕）");
    }
}
