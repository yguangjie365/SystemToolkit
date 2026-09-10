using System.Runtime.CompilerServices;

namespace SystemToolkit.Tests;

/// <summary>
/// 测试进程环境基线（2026-09-10 flaky 根治）。
/// <para>
/// 🔴 背景：沙箱 / 部分 CI 会注入 <c>HTTP_PROXY</c>（本机实测为 <c>127.0.0.1:64513</c>）。
/// 集成测试里的假上游同样跑在 127.0.0.1，若 HttpClient 把回环请求也交给该中间代理，
/// 会被它以 418 拒绝（首次请求被拒、重试却放行 → 表现为「单跑绿、全量随机红」的 flaky）。
/// 生产环境不存在此问题：音频代理有 host 白名单，回环地址根本不会进入转发链路。
/// 解法：测试进程内让回环直连（NO_PROXY），不影响任何指向真实外网的用例。
/// </para>
/// </summary>
internal static class TestEnvironment
{
    private const string LoopbackBypass = "127.0.0.1,localhost,[::1]";

    [ModuleInitializer]
    internal static void Init()
    {
        // 必须在任何 HttpClient 首次使用（HttpClient.DefaultProxy 初始化）之前设置
        Environment.SetEnvironmentVariable("NO_PROXY", LoopbackBypass);
        Environment.SetEnvironmentVariable("no_proxy", LoopbackBypass);
    }
}
