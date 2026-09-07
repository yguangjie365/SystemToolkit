using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// wininet 刷新通知（P/Invoke，系统边界不做单测）。
/// <para>
/// 【坑·设计文档 v0.2 §4.3】只写注册表（ProxyEnable / ProxyServer）浏览器<b>不会感知</b>，
/// 必须紧接调用 <c>InternetSetOption(NULL, INTERNET_OPTION_SETTINGS_CHANGED)</c> 与
/// <c>InternetSetOption(NULL, INTERNET_OPTION_REFRESH)</c>，否则表现为
/// 「代理开关拨了没反应，重开浏览器才生效」——本复盘项目最恨的静默失败类别。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinInetInterop
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    /// <summary>
    /// 刷新失败时的日志回调。
    /// 【P2 R4 线程安全文档】静态属性无锁同步，使用时必须严格遵守以下约束：
    /// <list type="bullet">
    /// <item><b>非线程安全：</b>两个并发调用 <c>SetSystemProxy</c> 时，第二次赋值会覆盖第一次的
    /// 回调引用，日志串号到对方输出。当前调用拓扑（UI 串行、用户一次只拨一个代理开关）
    /// 是安全的，但<b>禁止把本类用于并行/后台批量刷新</b>。</item>
    /// <item><b>禁止永久赋值：</b>必须「进入作用域前暂挂原值 → finally 还原原值」（
    /// 参考 <c>NetworkInfoService.SetSystemProxy</c> 的 try/finally 模式）；若回调持有
    /// 作用域对象引用且未还原，后续调用会触发生命周期错位（可能 NRE 或日志错位）。</item>
    /// <item><c>null</c> 时安全退化：静默（与未接线时的历史行为一致，不破坏）。</item>
    /// </list>
    /// </summary>
    internal static Action<string>? Log { get; set; }

    /// <summary>
    /// 通知 WinINET「设置已变更」并强制刷新——写完代理注册表后必须立即调用，
    /// 否则浏览器/系统代理表现为「开关拨了没反应，重开进程才生效」（设计文档 v0.2 §4.3 的大坑）。
    /// <para>【S1】P/Invoke 返回值不再丢弃：失败经 <see cref="Log"/> 回调留痕（接线方提供的日志面板可见）。</para>
    /// </summary>
    internal static void RefreshSettings()
    {
        bool settingsChanged = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        bool refreshed = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        if (!settingsChanged || !refreshed)
        {
            Log?.Invoke($"WinINET 刷新未完全成功（settingsChanged={settingsChanged}, refreshed={refreshed}）——若代理未生效请重启浏览器");
        }
    }
}
