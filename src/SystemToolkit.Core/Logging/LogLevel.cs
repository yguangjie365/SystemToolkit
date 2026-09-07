namespace SystemToolkit.Core.Logging;

/// <summary>
/// 日志级别（数值越大越严重，用于最小值过滤）。
/// </summary>
public enum LogLevel
{
    /// <summary>最细粒度：循环内、每包、每次探测。默认关闭，<c>--diag</c> 才开。</summary>
    Trace = 0,

    /// <summary>调试用：方法进出、关键分支。默认关闭。</summary>
    Debug = 1,

    /// <summary>常规信息：服务启停、任务完成计数。默认级别。</summary>
    Info = 2,

    /// <summary>警告：可恢复但值得注意（降级、重试、超时）。</summary>
    Warn = 3,

    /// <summary>错误：单项操作失败，但进程继续。</summary>
    Error = 4,

    /// <summary>致命：进程即将终止或已无法继续（未处理异常、启动失败）。</summary>
    Fatal = 5,
}
