using SystemToolkit.Core.Logging;

namespace SystemToolkit.Core.Contracts;

/// <summary>最小日志契约：Core 层统一入口，具体落盘/接入 Serilog 由实现方决定。</summary>
/// <remarks>
/// 2026-09-06 日志系统改造：新增 <see cref="Log(LogLevel, string, Exception?)"/> 与
/// <see cref="IsEnabled(LogLevel)"/>，二者都提供<b>默认实现</b>——
/// 这样既有实现（含测试假件）一行都不用改即可编译通过，新实现覆写后即可享受级别过滤。
/// 默认实现把 Trace/Debug 降级记为 Info（宁可记多也不丢），老实现行为不变。
/// </remarks>
public interface ILogger
{
    /// <summary>
    /// 来源标签（模块 id / 子系统名）。默认 "app"——既有实现与测试假件零破坏，
    /// BusLogger / FileLogger 覆写为自身真实来源（LOG-2，2026-09-11，供 Time() 助手取用）。
    /// </summary>
    string Source => "app";

    /// <summary>记录警告（可恢复的异常状态）。</summary>
    void Warn(string message);

    /// <summary>记录错误，可附带异常对象。</summary>
    void Error(string message, Exception? ex = null);

    /// <summary>记录常规信息。</summary>
    void Info(string message);

    /// <summary>
    /// 按级别记录。默认实现按级别映射到 Info/Warn/Error 三个老方法。
    /// </summary>
    void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        if (level >= LogLevel.Error)
        {
            Error(message, ex);
        }
        else if (level == LogLevel.Warn)
        {
            Warn(message);
        }
        else
        {
            Info(message);
        }
    }

    /// <summary>
    /// 该级别当前是否会真正落盘。调用方可用它跳过昂贵的字符串拼接。
    /// 默认实现：Trace/Debug 关，其余开（与「默认 INFO」策略一致）。
    /// </summary>
    bool IsEnabled(LogLevel level) => level >= LogLevel.Info;
}
