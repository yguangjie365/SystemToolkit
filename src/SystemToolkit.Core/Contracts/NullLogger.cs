namespace SystemToolkit.Core.Contracts;

/// <summary>空日志实现：所有写入均为无操作，供无日志需求的调用方注入。</summary>
public sealed class NullLogger : ILogger
{
    /// <summary>全局共享单例。</summary>
    public static readonly NullLogger Instance = new NullLogger();

    private NullLogger()
    {
    }

    /// <summary>固定为 "null"（<c>Time()</c> 助手据此整链静默，不绕过 NullLogger 往总线写）。</summary>
    public string Source => "null";

    /// <summary>无操作。</summary>
    public void Warn(string message)
    {
    }

    /// <summary>无操作。</summary>
    public void Error(string message, Exception? ex = null)
    {
    }

    /// <summary>无操作。</summary>
    public void Info(string message)
    {
    }
}
