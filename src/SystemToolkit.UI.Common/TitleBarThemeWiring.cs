using System.Windows;
using System.Windows.Interop;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.UI.Common;

/// <summary>
/// 窗口标题栏（非客户区）跟随主题明暗的**共享接线器** —— 2026-09-15 深色主题加固的任务二。
/// <para>
/// <b>为什么需要它</b>：互操作本体在 <see cref="WindowTitleBarTheme"/>（Core），但"何时调用"
/// 是每个窗口自己的事：句柄就绪时要套一次（<see cref="Window.SourceInitialized"/>）、
/// 主题切换后要再套一次（<see cref="ThemeManager.ThemeChanged"/>）、窗口关闭要退订。
/// 这三件事在宿主 + 8 个模块级弹窗上**完全一样**——按 AGENTS §四「同一结构出现 2 次以上必须抽为
/// 共享组件」，收在这里一份，而不是 9 份复制粘贴（复制粘贴的第 9 份迟早会漏掉退订那一步）。
/// </para>
/// <para>
/// <b>退订（🔴 本类的核心职责）</b>：<see cref="ThemeManager.ThemeChanged"/> 是**静态**事件；
/// 订阅它 = 静态事件持有闭包 → 闭包持有 <see cref="Window"/>。不退订的后果有两条，
/// 且都不是"理论上"：① 窗口关闭后每次切主题仍会回调到这个已死窗口；
/// ② 关闭的窗口连同其可视树被静态事件钉住（迷你播放器可反复开关，泄漏会累积）。
/// 故 <see cref="Window.Closed"/> 时三个订阅**全部**摘掉（<c>-=</c> 幂等，重复调用无害）。
/// </para>
/// <para>
/// 🔴 <b>失败必须静默降级</b>（与 <see cref="WindowTitleBarTheme"/> 同口径）：本方法挂在窗口打开路径与
/// 主题切换路径上，任何异常都会变成"窗口打不开"或"切主题炸"。故内部兜底 + 留一条 Warn，
/// **不抛、不阻断**。<c>SourceInitialized</c> 的处理器会在 <see cref="Window.Show"/> 内部被调用，
/// 从那里抛出去的异常会让窗口根本显示不出来——这正是本类要防的。
/// </para>
/// <para>
/// <b>无标题栏窗口（<c>WindowStyle="None" AllowsTransparency="True"</c>：SteamApiKeyWindow /
/// MiniPlayerWindow）照样接线，不做特判</b>：这类窗口没有非客户区，DWM 属性无处可画 ⇒ 调用自然成为空操作。
/// 特判（读 <see cref="Window.WindowStyle"/> 提前 return）看起来更"省"，实则是把一条隐式策略埋进共享组件：
/// 窗口哪天去掉 <c>WindowStyle="None"</c>（或 <c>AllowsTransparency</c> 被撤），接线**不会**自动跟上，
/// 而症状是"标题栏又不跟随了"这种没人会联想到接线器的静默退化。统一路径没有这个漂移面。
/// </para>
/// </summary>
public static class TitleBarThemeWiring
{
    /// <summary>主题日志模块名（与 <see cref="ThemeManager"/> / <see cref="WindowTitleBarTheme"/> 同源）。</summary>
    private const string LogModule = "theme";

    /// <summary>
    /// 让该窗口的标题栏跟随当前主题明暗，并持续跟随后续切换。
    /// <para>
    /// 🔴 调用点：窗口**构造期**（<c>InitializeComponent()</c> 之后）调用一次即可，句柄无需就绪——
    /// 句柄为零时套用是安全的空操作，真正的首次套用由
    /// <see cref="Window.SourceInitialized"/> 完成。不要在 <c>Show</c>/<c>OnSourceInitialized</c> 里重复接线
    /// （重复接线不会崩，但会留下多余的订阅）。
    /// </para>
    /// </summary>
    /// <param name="window">目标窗口；为 <see langword="null"/> 抛 <see cref="ArgumentNullException"/>（编程错误，不属"静默降级"范围）。</param>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        void OnSourceInitialized(object? sender, EventArgs e) => ApplyQuietly(window);
        void OnThemeChanged() => ApplyQuietly(window);
        void OnClosed(object? sender, EventArgs e)
        {
            // 🔴 必须退订：静态事件 → 闭包 → Window，不退订即泄漏（详见类摘要）。
            ThemeManager.ThemeChanged -= OnThemeChanged;
            window.SourceInitialized -= OnSourceInitialized;
            window.Closed -= OnClosed;
        }

        window.SourceInitialized += OnSourceInitialized;
        ThemeManager.ThemeChanged += OnThemeChanged;
        window.Closed += OnClosed;

        // 句柄可能已经就绪（Attach 晚于 Show，或窗口已被 EnsureHandle）：立刻套一次。
        // 未就绪时 WindowInteropHelper.Handle 返回 0 → 下游直接返回 false，不算故障、不留日志。
        ApplyQuietly(window);
    }

    /// <summary>
    /// 套用当前主题的标题栏明暗；**任何失败都只留一条 Warn**（不抛、不阻断窗口打开与主题切换）。
    /// </summary>
    private static void ApplyQuietly(Window window)
    {
        try
        {
            // Handle 取值在窗口已释放 / Dispatcher 已停时可能抛，故与 DWM 调用同处一层兜底。
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle != nint.Zero)
            {
                WindowTitleBarTheme.TryApplyDarkMode(handle, ThemeManager.IsDarkTheme);
            }
        }
        catch (Exception ex)
        {
            // 降级留痕：标题栏只是"外观跟随"，坏掉不影响窗口可用——故不重试、不打断，
            // 但不能无声（AGENTS §三「禁止静默失败」的口径是"显式提示或留痕"，此处取留痕）。
            AppLog.Write(LogEntry.Create(
                LogLevel.Warn, LogModule,
                $"窗口「{window.GetType().Name}」标题栏明暗跟随失败（客户区主题不受影响，窗口照常使用）", ex));
        }
    }
}
