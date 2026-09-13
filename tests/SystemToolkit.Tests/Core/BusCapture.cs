using SystemToolkit.Core.Logging;

namespace SystemToolkit.Tests;

/// <summary>
/// 挂到 <see cref="AppLog"/> 总线的临时内存落点（LOG 系列测试共享）。
/// xunit.runner.json 全串行（parallelizeTestCollections=false），跨用例装/摘总线落点安全；
/// 用完必须 <see cref="AppLog.Reset"/>（RecordAsync 已兜底）。
/// </summary>
internal sealed class BusCapture : ILogSink
{
    private readonly object _gate = new();

    private readonly List<LogEntry> _entries = new();

    /// <summary>已捕获记录的**只读快照**。</summary>
    /// <remarks>
    /// 🔴 必须是快照，不能是裸 <see cref="List{T}"/>：<see cref="Emit"/> 会在传输回调线程上被调用,
    /// 而用例往往在"任务完成"信号一到就立刻枚举 —— 那一刻可能还有收尾日志在写，
    /// 裸 List 会抛 <c>InvalidOperationException: Collection was modified</c>
    /// （2026-09-14 全量实测踩中一次；此前一直是靠时序侥幸通过）。
    /// 快照把"遍历期间集合不变"变成结构保证。
    /// </remarks>
    public List<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return new List<LogEntry>(_entries);
            }
        }
    }

    /// <summary>已捕获条数（供轮询"日志是否还在增长"使用，不复制集合）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Emit(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

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
