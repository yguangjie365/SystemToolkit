using System.Collections.Concurrent;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Core.Logging;

/// <summary>
/// 异常日志限流器：同一「类型 + 首帧」的异常只完整记录首次，后续只累计次数。
/// <para>
/// 为什么必须有：渲染循环类的异常会以每帧一次的频率触发首次异常回调，
/// 无条件记录等于把「日志」变成「故障」（旧工程 4 分钟 736MB 就是这么来的）。
/// 去重后：首次留完整堆栈（可定位），重复次数单独记账（可知严重性），两者都不丢。
/// </para>
/// <para>限流只作用于<b>高频自动记录</b>（首次异常回调）。显式 <c>logger.Error(...)</c> 走正常路径，不受影响。</para>
/// </summary>
public sealed class ExceptionLogThrottle
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly ILogger _logger;
    private readonly int _maxDistinct;
    private int _overflowReported;

    /// <summary>构造限流器。</summary>
    /// <param name="logger">输出目标。</param>
    /// <param name="maxDistinct">最多跟踪多少种不同异常；超出后新种类只计数（防字典无限膨胀）。</param>
    public ExceptionLogThrottle(ILogger logger, int maxDistinct = 200)
    {
        _logger = logger;
        _maxDistinct = maxDistinct;
    }

    /// <summary>
    /// 判断该异常是否应当完整记录。
    /// </summary>
    /// <returns>true = 首次出现，应记录完整堆栈；false = 已去重，仅计数。</returns>
    public bool ShouldLog(Exception ex)
    {
        string key = KeyOf(ex);

        if (_buckets.TryGetValue(key, out Bucket? bucket))
        {
            bucket.Increment();
            return false;
        }

        if (_buckets.Count >= _maxDistinct)
        {
            ReportOverflowOnce();
            return false;
        }

        return _buckets.TryAdd(key, new Bucket(ex));
    }

    /// <summary>
    /// 输出去重汇总（哪些异常被吞了多少次）。退出前或定时调用。
    /// 只有被吞过（次数 &gt; 1）的才输出，避免噪音。
    /// </summary>
    public void FlushSummary()
    {
        foreach (KeyValuePair<string, Bucket> kv in _buckets)
        {
            if (kv.Value.Count <= 1)
            {
                continue;
            }

            _logger.Warn($"[异常限流] {kv.Key} 共 {kv.Value.Count} 次（{kv.Value.FirstAt:HH:mm:ss} 起，"
                         + $"末次 {kv.Value.LastAt:HH:mm:ss}），仅首次记录完整堆栈");
        }
    }

    /// <summary>清空记账。</summary>
    public void Reset() => _buckets.Clear();

    private void ReportOverflowOnce()
    {
        if (Interlocked.Exchange(ref _overflowReported, 1) == 1)
        {
            return;
        }

        _logger.Warn($"[异常限流] 不同异常种类已达上限 {_maxDistinct}，后续新异常只计数不记录。");
    }

    private static string KeyOf(Exception ex)
    {
        string? top = ex.StackTrace?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return $"{ex.GetType().FullName} @ {top ?? "<no-stack>"}";
    }

    private sealed class Bucket(Exception ex)
    {
        private int _count = 1;

        public int Count => Volatile.Read(ref _count);

        public DateTime FirstAt { get; } = DateTime.Now;

        public DateTime LastAt { get; private set; } = DateTime.Now;

        /// <summary>保留异常对象只为 Debug 期排查；生产路径不读它。</summary>
        public Exception Sample { get; } = ex;

        public void Increment()
        {
            Interlocked.Increment(ref _count);
            LastAt = DateTime.Now;
        }
    }
}
