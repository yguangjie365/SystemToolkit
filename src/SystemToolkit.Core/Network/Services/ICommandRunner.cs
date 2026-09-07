namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 进程执行抽象（直连目标程序，<b>不经 cmd</b>）。让 Core 网络服务依赖接口而非具体实现，
/// 单元测试可用 fake 替身记录 (fileName, arguments) 并可编程返回退出码——
/// 彻底摆脱对真实 netsh 进程的依赖（先例：<c>IWingetClient</c>）。
/// </summary>
public interface ICommandRunner
{
    /// <summary>
    /// 运行一条命令并逐行回传输出。返回进程退出码；超时 / 外部取消时终止子进程并返回 -1。
    /// </summary>
    /// <param name="fileName">目标程序（如 <c>netsh</c> / <c>ipconfig</c>），不含路径由系统 PATH 解析。</param>
    /// <param name="arguments">参数串。netsh 是自带解析器的命令行（要保留 <c>name="…"</c> 内嵌引号语义），
    /// 因此传<b>整串</b>而非逐参数列表——与 winget 的 ArgumentList 方式刻意不同，见 <see cref="NetshArgs"/>。</param>
    /// <param name="onLine">输出回调（stdout + stderr 逐行合并），供 VM 挂接日志面板。</param>
    /// <param name="ct">外部取消令牌。</param>
    /// <param name="timeout">【审查修复】命令超时（超时后终止整个进程树并返回 -1）。
    /// null = 不限——<b>调用方必须显式传入</b>：外部进程可能无限挂起
    ///（如 DHCP 服务器无响应时 <c>ipconfig /renew</c> 可阻塞 30s+），不设超时会锁死 UI 防重入。</param>
    Task<int> RunAsync(string fileName, string arguments, Action<string> onLine, CancellationToken ct = default, TimeSpan? timeout = null);
}
