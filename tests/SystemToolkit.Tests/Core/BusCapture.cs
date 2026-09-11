using SystemToolkit.Core.Logging;

namespace SystemToolkit.Tests;

/// <summary>
/// 挂到 <see cref="AppLog"/> 总线的临时内存落点（LOG 系列测试共享）。
/// xunit.runner.json 全串行（parallelizeTestCollections=false），跨用例装/摘总线落点安全；
/// 用完必须 <see cref="AppLog.Reset"/>（RecordAsync 已兜底）。
/// </summary>
internal sealed class BusCapture : ILogSink
{
    public List<LogEntry> Entries { get; } = new();

    public void Emit(LogEntry entry) => Entries.Add(entry);

    /// <summary>复位总线→装捕获→跑 body→复位收口，返回捕获到的全部记录。</summary>
    public static async Task<List<LogEntry>> RecordAsync(Func<Task> body)
    {
        AppLog.Reset();
        var capture = new BusCapture();
        AppLog.AddSink(capture);
        try
        {
            await body();
        }
        finally
        {
            AppLog.Reset();
        }

        return capture.Entries;
    }
}
