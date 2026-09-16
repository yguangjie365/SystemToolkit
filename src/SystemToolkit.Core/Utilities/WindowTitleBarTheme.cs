using System.Buffers.Binary;
using SystemToolkit.Core.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace SystemToolkit.Core.Utilities;

/// <summary>
/// 窗口标题栏（非客户区）跟随主题明暗 —— DWM 沉浸式深色模式（2026-09-15 深色主题加固）。
/// <para>
/// <b>为什么需要它</b>：WPF 只管客户区，标题栏与外框由 DWM 画。切到 <c>Nvidia.Dark</c> 后
/// 客户区全黑、标题栏仍是系统浅色 —— 这是深色主题下**可见度最高**的一处不一致，
/// 也是 ADR-005「每个主题包各自独立达标」在宿主窗口上的缺口（此前全仓 grep
/// <c>DwmSetWindowAttribute</c>/<c>DWMWA</c>/<c>ImmersiveDarkMode</c> 零命中）。
/// </para>
/// <para>
/// <b>为什么落在 Core 而不是 Shell</b>（2026-09-15 用户裁定）：AGENTS §二 硬规则「UI 层不得
/// 直接调用系统命令，必须经 Core 服务」——宿主只传一个窗口句柄，系统互操作留在本层；
/// 且 CsWin32 已在本工程登记在册（见 <c>NativeMethods.txt</c> 与 ADR-002 §5.3），
/// 不引入任何新的第三方依赖。入参是裸 <see cref="nint"/> 句柄，本类**不依赖任何 UI 框架**。
/// </para>
/// <para>
/// <b>属性号取值依据</b>：<c>dwmapi.h</c> 的 <c>DWMWINDOWATTRIBUTE</c> 只定义
/// <c>DWMWA_USE_IMMERSIVE_DARK_MODE = 20</c>（官方枚举页 2026-09-15 实查；本机
/// Win32 metadata 生成的枚举同样只有这一个成员）。社区常见的 <c>19</c>（1809–1903 用）
/// **不在**官方枚举内，按 AGENTS §二·五「一律抄参考源码或官方头文件，禁止凭记忆写常量」
/// **不予实现**：本机（Windows 11 build 26300）已由 <c>WindowTitleBarThemeSmokeTests</c>
/// 用真实 HWND 实测该属性被接受；旧系统不认它时调用失败 → 静默降级
/// （标题栏保持系统配色 + 一条 Warn 日志），不影响启动与主题切换。
/// </para>
/// <para>
/// 🔴 <b>失败一律静默降级</b>：旧系统不认该属性、或 DWM 合成被关闭时，本方法返回
/// <see langword="false"/> 并留一条 Warn（不抛异常、不阻断启动、不阻断主题切换）——
/// 与 <c>ThemeManager</c> 既有留痕口径一致（模块名 <c>theme</c>）。
/// </para>
/// </summary>
public static class WindowTitleBarTheme
{
    /// <summary>通用主题日志模块名（与 ThemeManager 同源，便于按 <c>theme</c> 过滤整条主题链路）。</summary>
    private const string LogModule = "theme";

    /// <summary>失败/不适用是否已留过痕（避免每次主题切换都刷同一条 Warn）。</summary>
    private static bool _degradeLogged;

    /// <summary>
    /// 把窗口标题栏切到指定明暗。<paramref name="dark"/> 为 <see langword="true"/> 时置深色。
    /// <para>
    /// 调用时机：窗口句柄可用之后（WPF 为 <c>SourceInitialized</c>/<c>OnSourceInitialized</c>）
    /// 与每次主题切换后。句柄为 0（尚未创建）时直接返回 <see langword="false"/>，不视为故障。
    /// </para>
    /// </summary>
    /// <param name="windowHandle">窗口句柄（WPF 取 <c>WindowInteropHelper.Handle</c>）。</param>
    /// <param name="dark">是否使用深色标题栏。</param>
    /// <returns>是否应用成功；失败/不适用返回 <see langword="false"/>（已留 Warn 痕迹）。</returns>
    public static bool TryApplyDarkMode(nint windowHandle, bool dark)
    {
        if (windowHandle == 0)
        {
            return false; // 句柄未就绪属正常时序（调用过早），不算降级，不刷日志
        }

        // 运行期守卫（同时满足 CA1416）：DWM 从 Vista 起可用，这里按本仓统一的最低支持版本
        // （Vista 6.0.6000）判定，与 FileLinker 的守卫口径一致——AGENTS §五 明确要求
        // Core 用运行期判断而非 [SupportedOSPlatform]（后者会让调用点整片报 CA1416）。
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 0, 6000))
        {
            LogDegradeOnce("当前平台无 DWM，标题栏明暗跟随不适用（客户区主题不受影响）");
            return false;
        }

        // pvAttribute 指向 BOOL（4 字节）：1 = 深色标题栏，0 = 始终浅色。
        // 经 CsWin32 生成的 ReadOnlySpan<byte> 友好重载传入，不手写签名与指针（AGENTS §二·五）。
        Span<byte> payload = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, dark ? 1 : 0);

        HRESULT result = PInvoke.DwmSetWindowAttribute(
            (HWND)windowHandle, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, payload);

        if (result.Succeeded)
        {
            return true;
        }

        LogDegradeOnce(
            $"DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE) 失败，标题栏保持系统配色"
            + $"（HRESULT=0x{result.Value:X8}；Win10 2004 之前的系统不认该属性）");
        return false;
    }

    /// <summary>降级留痕（同一进程只报一次，避免主题来回切换时刷屏）。</summary>
    private static void LogDegradeOnce(string message)
    {
        if (_degradeLogged)
        {
            return;
        }

        _degradeLogged = true;
        AppLog.Write(LogEntry.Create(LogLevel.Warn, LogModule, message));
    }
}
