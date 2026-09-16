using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using SystemToolkit.Core.Utilities;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 窗口标题栏主题冒烟（2026-09-15 深色主题加固的运行时验证）。
/// <para>
/// 背景：客户区由 WPF 渲染，标题栏（非客户区）由 DWM 渲染——切到 Nvidia.Dark 后
/// 客户区全黑、标题栏仍是系统浅色。修复见 <see cref="WindowTitleBarTheme"/>（Core），
/// 由 <c>MainWindow</c> 在 <c>SourceInitialized</c> 与 <c>ThemeManager.ThemeChanged</c> 时调用。
/// </para>
/// <para>
/// 本守卫把「DWM 调用是否真的通」钉进构建期：拿一个**真实 HWND**（非可见窗口，不会闪屏）
/// 实调 <c>DWM_USE_IMMERSIVE_DARK_MODE</c>，成功/失败双向都要覆盖——
/// 只断言"没抛异常"的验证等于没验证（03 §4.1）。
/// 无桌面会话（服务/headless）时 DWM 不可用，此时只跑句柄契约用例。
/// </para>
/// </summary>
public class WindowTitleBarThemeSmokeTests
{
    /// <summary>句柄未就绪（0）时必须安全返回 false：调用过早（窗口还没创建）不得抛异常。</summary>
    [Fact]
    public void WindowTitleBarTheme_ZeroHandle_ReturnsFalseWithoutThrowing()
    {
        Assert.False(WindowTitleBarTheme.TryApplyDarkMode(nint.Zero, dark: true));
    }

    /// <summary>
    /// 真实 HWND：深色与浅色方向都必须被 DWM 接受（应用成功返回 true）。
    /// 这同时验证了属性号、BOOL 载荷与 CsWin32 生成的调用签名三者匹配。
    /// </summary>
    [Fact]
    public void WindowTitleBarTheme_RealWindowHandle_BothDirectionsAccepted()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return; // 无桌面会话 → DWM 不可用，本机不适用（非产品缺陷）
        }

        (bool Dark, bool Light, Exception? Error) result = ApplyOnRealWindow();
        Assert.Null(result.Error);
        Assert.True(result.Dark, "深色标题栏应用失败：DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE=1) 未被接受");
        Assert.True(result.Light, "浅色标题栏回置失败：DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE=0) 未被接受");
    }

    /// <summary>
    /// 共享接线器（<see cref="TitleBarThemeWiring"/>）的订阅生命周期：<c>Attach</c> 对**静态**事件
    /// <see cref="ThemeManager.ThemeChanged"/> 净增一个订阅，窗口 <c>Closed</c> 后必须**回到基线**。
    /// <para>
    /// 为什么值得一条机器约束：静态事件持有闭包、闭包持有窗口——不退订就是"关掉的窗口被事件钉住"
    /// （迷你播放器可反复开关，泄漏会累积），而这类缺陷**肉眼不可见**、只靠读代码自觉。
    /// 只断言"没抛异常"等于没验证（03 §4.1），故这里断言计数**可增亦可减**这个可观测事实。
    /// 计数经反射读事件的后备字段——事件改名或改成显式 add/remove 时本用例会红，属预期（守卫要能跟上）。
    /// </para>
    /// </summary>
    [Fact]
    public void TitleBarThemeWiring_AttachThenClose_ReturnsSubscriberCountToBaseline()
    {
        int before = ThemeChangedSubscriberCount();
        int afterAttach = -1;
        int afterClose = -1;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = ViewLoadSmokeGuardTests.EnsureApplication();
                var window = new Window { Width = 200, Height = 120, ShowActivated = false };

                TitleBarThemeWiring.Attach(window);
                afterAttach = ThemeChangedSubscriberCount();

                window.Show();
                window.Close();
                afterClose = ThemeChangedSubscriberCount();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        Assert.Null(error);
        Assert.Equal(before + 1, afterAttach);  // 接线确实订阅了（否则"退订"断言毫无意义）
        Assert.Equal(before, afterClose);       // 🔴 Closed 必须退订：回到基线，静态事件不再持有该窗口
    }

    /// <summary>
    /// <see cref="ThemeManager.ThemeChanged"/> 的当前订阅者个数（读事件的后备字段）。
    /// 用作"订阅净增量"的观测点——只看**相对变化**，不受其它用例遗留订阅影响。
    /// </summary>
    private static int ThemeChangedSubscriberCount()
        => (typeof(ThemeManager)
                .GetField("ThemeChanged", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null) as Action)?.GetInvocationList().Length ?? 0;

    /// <summary>
    /// 在 STA 线程上用真实 HWND（<c>EnsureHandle</c>，不 Show → 不闪屏）双向调用一次。
    /// STA 与 Application 复用口径同 <c>WindowSmokeGuardTests</c>（WPF 单实例、测试串行配置见 xunit.runner.json）。
    /// </summary>
    private static (bool Dark, bool Light, Exception? Error) ApplyOnRealWindow()
    {
        bool dark = false;
        bool light = false;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = ViewLoadSmokeGuardTests.EnsureApplication();
                var window = new Window { Width = 200, Height = 120, ShowActivated = false };
                nint handle = new WindowInteropHelper(window).EnsureHandle();

                dark = WindowTitleBarTheme.TryApplyDarkMode(handle, dark: true);
                light = WindowTitleBarTheme.TryApplyDarkMode(handle, dark: false);

                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        return (dark, light, error);
    }
}
