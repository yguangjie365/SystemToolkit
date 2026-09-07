namespace SystemToolkit.Core.Logging;

/// <summary>
/// 日志落点（sink）：把 <see cref="LogEntry"/> 写到某处（文件 / 内存 / UI 面板 / 转发到 Serilog）。
/// <para>
/// 实现必须自保：<b>不得抛异常</b>。日志通道自身抛出异常会在最需要它的时候把程序带崩
/// （历史上 NullLogger 吞异常、日志写失败引发过多次次生事故）。
/// 总线 <see cref="AppLog"/> 也会再兜一层 try/catch。
/// </para>
/// </summary>
public interface ILogSink
{
    /// <summary>处理一条记录。实现类内部须捕获一切异常。</summary>
    void Emit(LogEntry entry);
}
