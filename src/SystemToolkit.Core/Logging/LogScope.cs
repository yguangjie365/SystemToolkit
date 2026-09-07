namespace SystemToolkit.Core.Logging;

/// <summary>
/// 日志关联作用域：把「一次用户操作」跨模块产生的日志串成一条链。
/// <para>
/// 用 <see cref="AsyncLocal{T}"/> 承载，故能跨 await 延续传递（不像 ThreadLocal 会在线程切换后丢失）。
/// 典型用法：<c>using (logger.BeginScope("backup")) { … }</c>——作用域内所有日志自动带上同一个 cid，
/// 排查时grep 一个 cid 就能看到这次操作在 Overview / Core / Infrastructure 各层的完整轨迹。
/// </para>
/// </summary>
public static class LogScope
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>当前关联 ID（无作用域时为 null）。</summary>
    public static string? CorrelationId
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    /// <summary>生成一个新的短关联 ID（8 位十六进制，够排查用，不占版面）。</summary>
    public static string NewId() => Guid.NewGuid().ToString("n")[..8];

    /// <summary>
    /// 开启一个作用域；返回值 Dispose 时恢复上一层（可嵌套）。
    /// </summary>
    public static IDisposable Begin(string? correlationId = null)
    {
        string id = correlationId ?? NewId();
        string? previous = Current.Value;
        Current.Value = id;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = previous;
        }
    }
}
