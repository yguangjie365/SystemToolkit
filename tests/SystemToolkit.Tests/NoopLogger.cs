using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Tests;

/// <summary>全测试工程共享的空 logger 假件（只吞不记；需要断言告警时用各自的 CapturingLogger）。</summary>
public sealed class NoopLogger : ILogger
{
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
    public void Info(string message) { }
}
