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
            // 🟠 V14-U2：封送出去的 lambda 原先完全裸露——集合写入异常会在 UI 线程上直冲
            // DispatcherUnhandledException（全局处理器吞掉并计数，5s 内超 100 条才放行），
            // 表现为"日志少了一行且完全无线索"。就地兜底，并落到进程日志总线（见 LogFeedFailure）。
            dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Append(lines, message, maxLines);
                }
                catch (Exception ex)
                {
                    LogFeedFailure("追加", ex);
                }
            });
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
            // 🟠 V14-U2：同 Append 的封送分支——lambda 内兜底（清空失败同样不得直冲 Dispatcher）。
            dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Clear(lines);
                }
                catch (Exception ex)
                {
                    LogFeedFailure("清空", ex);
                }
            });
            return;
        }

        lines.Clear();
    }

    /// <summary>
    /// 日志面板写入失败的唯一落点。
    /// 🔴 **只走进程日志总线（<see cref="SystemToolkit.Core.Logging.AppLog"/>，落点是文件）** ——
    /// 绝不能再回到 <see cref="Append"/> / <see cref="Clear"/>：本次异常正来自那次集合写入，
    /// 再调一次就是递归（同一集合、同一故障，一路套下去）。总线落点与 UI 集合无交集，
    /// 且其实现自保不抛（<c>ILogSink</c> 契约），可安全用作终态兜底。
    /// </summary>
    private static void LogFeedFailure(string operation, Exception ex)
        => SystemToolkit.Core.Logging.AppLog.Write(SystemToolkit.Core.Logging.LogEntry.Create(
            SystemToolkit.Core.Logging.LogLevel.Warn, "ui",
            $"日志面板{operation}失败（该条已丢弃，不影响其它日志）：{ex.Message}", ex));
}
