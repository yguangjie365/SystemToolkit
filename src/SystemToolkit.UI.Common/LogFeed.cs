using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace SystemToolkit.UI.Common;

/// <summary>
/// 日志集合的公共维护逻辑：头部插入最新行、超出上限裁剪尾部、跨线程自动封送 UI。
/// 复用自旧工程 UI.Common（原文迁移）。
/// 审查 2026-09-04（P2）：Application.Current 在启动极早期为 null，原实现退化成
/// 跨线程直改集合（InvalidOperationException）——现在记忆首次可用的 Dispatcher 兜底。
/// </summary>
public static class LogFeed
{
    public const int DefaultMaxLines = 500;

    private static Dispatcher? _fallbackDispatcher;

    private static Dispatcher? DispatcherFor()
    {
        if (Application.Current?.Dispatcher is { } app)
        {
            _fallbackDispatcher ??= app;
            return app;
        }

        if (_fallbackDispatcher is { } cached)
        {
            return cached;
        }

        // 启动极早期：退回当前线程的 Dispatcher（首次调用通常就在 UI 线程）
        var current = Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
        if (current is not null)
        {
            _fallbackDispatcher = current;
            return current;
        }

        return null; // 无任何 Dispatcher 可用：调用方处于单线程阶段，直接同线程操作
    }

    /// <summary>在集合头部插入一行并裁剪到上限；可从任意线程调用（非 UI 线程自动封送）。</summary>
    public static void Append(ObservableCollection<LogLine> lines, string message, int maxLines = DefaultMaxLines)
    {
        Dispatcher? dispatcher = DispatcherFor();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => Append(lines, message, maxLines));
            return;
        }

        lines.Insert(0, LogLine.Create(message));
        while (lines.Count > maxLines)
        {
            lines.RemoveAt(lines.Count - 1);
        }
    }

    /// <summary>清空集合；可从任意线程调用（非 UI 线程自动封送）。</summary>
    public static void Clear(ObservableCollection<LogLine> lines)
    {
        Dispatcher? dispatcher = DispatcherFor();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => Clear(lines));
            return;
        }

        lines.Clear();
    }
}
