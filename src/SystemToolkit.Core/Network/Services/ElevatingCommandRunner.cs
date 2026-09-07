using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using SystemToolkit.Core.Drivers;
using SystemToolkit.Core.Elevated;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="ICommandRunner"/> 提权装饰器：读命令直连（不触发 UAC）；命中
/// <see cref="NetshTokenRules.IsElevatedWrite"/> 白名单的写命令经 Elevated Helper 进程
/// （verb=runas → UAC）执行。老服务（NetConfigService / NetRepairService / TcpTuningService）
/// 依赖注入本装饰器即获得「按需 UAC」能力，服务代码零改动——
/// 旧工程假设应用整体提权（requireAdministrator），本装饰器是新宿主非提权架构的适配层。
/// <para>
/// 🔴 用户拒绝 UAC（Win32 1223）→ 退出码 1223 返回调用方，流程安全终止、无副作用（03 设计 §6）：
/// bounce 的 enable 被拒 → 网卡停留禁用态，老服务内置的重试与「请到设备管理器手动启用」提示兜底。
/// </para>
/// <para>
/// ⚠️ 每条写命令一次 UAC（helper 分段批执行通道保留，V0.4 未接批合并——已知 UX 摩擦，
/// 若用户反馈弹窗频繁再做操作级合并）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatingCommandRunner : ICommandRunner
{
    /// <summary>调用方未显式传 timeout 时的兜底（与老服务写命令 60s 约定一致）。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly ICommandRunner _inner;
    private readonly string _helperPath;
    private readonly Func<string> _tempDirectoryProvider;

    /// <summary>注入非提权内层、Helper 程序路径与临时目录提供器（测试可替换）。</summary>
    public ElevatingCommandRunner(
        ICommandRunner inner,
        string? helperPath = null,
        Func<string>? tempDirectoryProvider = null)
    {
        _inner = inner;
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "SystemToolkit.ElevatedHelper.exe");
        _tempDirectoryProvider = tempDirectoryProvider ?? Path.GetTempPath;
    }

    /// <inheritdoc cref="ICommandRunner.RunAsync"/>
    public async Task<int> RunAsync(
        string fileName, string arguments, Action<string> onLine, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        // 白名单外（全部读命令 + 不认识的形状）直连执行——行为与非提权 CommandRunner 完全一致
        if (!NetshTokenRules.IsElevatedWrite(fileName, arguments))
        {
            return await _inner.RunAsync(fileName, arguments, onLine, ct, timeout).ConfigureAwait(false);
        }

        onLine("[提权] 该操作需要管理员权限，正通过提权辅助进程执行（将弹出 UAC 确认）");
        return await RunElevatedAsync("net", [fileName, arguments], onLine, timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写 HKLM <c>NetworkThrottlingIndex</c>（经 Helper throttling 窄白名单动词）。
    /// HKLM 直写在非提权主进程必失败（旧工程应用整体提权所以能直写）——这是
    /// TcpTuningService 经注入委托 <c>throttlingWriter</c> 走到的适配点。
    /// </summary>
    public Task<int> RunThrottlingWriteAsync(uint value, Action<string> onLine, CancellationToken ct = default)
    {
        onLine($"$ [注册表] HKLM\\…\\Tcpip\\Parameters\\NetworkThrottlingIndex = 0x{value:X}（经提权辅助进程）");
        return RunElevatedAsync("throttling", [$"0x{value:X}"], onLine, TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>经 Helper 执行单个子命令（verb + 参数列表）。退出码语义与直连一致（0 = 成功）。</summary>
    private async Task<int> RunElevatedAsync(
        string verb, IReadOnlyList<string> verbArgs, Action<string> onLine, TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(_helperPath))
        {
            onLine($"[提权] ❌ 提权辅助进程缺失：{_helperPath}");
            return -1;
        }

        string outFile = Path.Combine(_tempDirectoryProvider(), $"stk_net_{Guid.NewGuid():N}.out");
        // Helper 协议：--out <文件> <verb> <参数…>（含空格 token 按 Quote 规则包裹，同驱动通道）
        var psi = new ProcessStartInfo(_helperPath)
        {
            UseShellExecute = true, // verb=runas 必须走 shell 启动通道
            Verb = "runas",
            Arguments = string.Join(' ', new[] { "--out", Quote(outFile), verb }
                .Concat(verbArgs.Select(Quote))),
        };

        using Process proc = new() { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            onLine("[提权] ❌ 用户拒绝了 UAC 提权请求，操作已安全终止（无副作用）");
            return 1223;
        }

        // 客户端超时 = 调用方 timeout + 10s 缓冲：正常完成不被竞态误杀，挂死仍会被收尾逻辑终止整树
        ElevatedRunResult result = await ElevatedProcessFinisher
            .FinishAsync(proc, outFile, timeout.Add(TimeSpan.FromSeconds(10)), ct).ConfigureAwait(false);

        // Helper 把子进程输出（经 OutputDecoder 择优解码）写结果文件回传，逐行进日志
        OutputDecoder.ForEachLine(result.Output, onLine);
        return result.ExitCode;
    }

    /// <summary>Windows argv 规则引号包裹（委托驱动通道同源实现，杜绝两份规则漂移）。</summary>
    private static string Quote(string arg) => ElevatedPnpUtilClient.Quote(arg);
}
