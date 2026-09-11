using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="INetConfigService"/> 实现：全部经 netsh，命令构造收口在 <see cref="NetshArgs"/>。
/// 命令先回显（<c>$ netsh …</c>）再执行——回显既是日志可观测性，也是
/// 「未提权时从日志复制命令自救」路径的数据源（设计文档 §7）。
/// </summary>
public sealed class NetConfigService : INetConfigService
{
    private readonly ICommandRunner _runner;
    private readonly ILogger _logger;

    /// <summary>构造；命令执行器经接口注入（测试用 fake 记录命令行、可编程退出码）；日志可选（LOG-3）。</summary>
    public NetConfigService(ICommandRunner runner, ILogger? logger = null)
    {
        _runner = runner;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc cref="INetConfigService.SetDhcpAsync"/>
    public Task<int> SetDhcpAsync(string adapter, Action<string> onLine)
        => CompleteExitAsync(_logger.Time("SetDhcp"),
            RunAsync(() => NetshArgs.SetDhcp(adapter), onLine), $"网卡「{adapter}」恢复 DHCP");

    /// <inheritdoc cref="INetConfigService.SetStaticIpAsync"/>
    public async Task<int> SetStaticIpAsync(string adapter, string ip, string mask, string? gateway, Action<string> onLine)
    {
        LogTiming timing = _logger.Time("SetStaticIp");
        try
        {
            RequireIPv4(ip, nameof(ip));
            RequireIPv4(mask, nameof(mask));
            if (gateway is not null)
            {
                RequireIPv4(gateway, nameof(gateway));
            }
        }
        catch (ArgumentException ex)
        {
            timing.Complete(LogResult.Rejected, LogLevel.Warn, $"静态 IP 参数被拒：{ex.Message}");
            throw;
        }

        return await CompleteExitAsync(timing,
            RunAsync(() => NetshArgs.SetStaticIp(adapter, ip, mask, gateway), onLine),
            $"网卡「{adapter}」静态 IP {ip}").ConfigureAwait(false);
    }

    /// <inheritdoc cref="INetConfigService.SetDnsAsync"/>
    public async Task<int> SetDnsAsync(string adapter, string? primary, string? secondary, Action<string> onLine)
    {
        LogTiming timing = _logger.Time("SetDns");
        if (primary is null)
        {
            // 恢复自动：备用 DNS 一并交给 DHCP，忽略 secondary
            return await CompleteExitAsync(timing,
                RunAsync(() => NetshArgs.SetDnsToDhcp(adapter), onLine),
                $"网卡「{adapter}」DNS 恢复自动").ConfigureAwait(false);
        }

        // 两段校验都放在执行之前：不出现「主 DNS 已应用、备用格式非法抛异常」的半套状态
        try
        {
            RequireIPv4(primary, nameof(primary));
            if (secondary is not null)
            {
                RequireIPv4(secondary, nameof(secondary));
            }
        }
        catch (ArgumentException ex)
        {
            timing.Complete(LogResult.Rejected, LogLevel.Warn, $"DNS 参数被拒：{ex.Message}");
            throw;
        }

        int exit = await RunAsync(() => NetshArgs.SetDnsPrimary(adapter, primary), onLine).ConfigureAwait(false);
        if (exit != 0 || secondary is null)
        {
            timing.Complete(exit == 0 ? LogResult.Success : LogResult.Failed,
                exit == 0 ? LogLevel.Info : LogLevel.Warn,
                $"网卡「{adapter}」DNS {primary}：退出码 {exit}");
            return exit;
        }

        return await CompleteExitAsync(timing,
            RunAsync(() => NetshArgs.AddDnsSecondary(adapter, secondary), onLine),
            $"网卡「{adapter}」DNS {primary}+{secondary}").ConfigureAwait(false);
    }

    /// <inheritdoc cref="INetConfigService.SetAdapterEnabledAsync"/>
    public Task<int> SetAdapterEnabledAsync(string adapter, bool enabled, Action<string> onLine)
        => CompleteExitAsync(_logger.Time(enabled ? "EnableAdapter" : "DisableAdapter"),
            RunAsync(() => NetshArgs.SetAdapterEnabled(adapter, enabled), onLine),
            $"网卡「{adapter}」{(enabled ? "启用" : "禁用")}");

    private static async Task<int> CompleteExitAsync(LogTiming timing, Task<int> run, string what)
    {
        int exit = await run.ConfigureAwait(false);
        timing.Complete(exit == 0 ? LogResult.Success : LogResult.Failed,
            exit == 0 ? LogLevel.Info : LogLevel.Warn,
            $"{what}：退出码 {exit}");
        return exit;
    }

    // netsh 配置类命令统一超时：防进程挂起锁死 UI
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(Configuration.AppConstants.NetshCommandTimeoutSeconds);

    /// <summary>
    /// 命令构造收进防线内（【审查修复 2.1】）：NetshArgs 构造可能抛 ArgumentException
    /// （适配器名含引号），工厂延迟求值保证异常在 try 内被捕获 → 降级返回，
    /// 不穿透到 VM。若在调用点先行求值再传入，防线就形同虚设（实测踩过）。
    /// </summary>
    private async Task<int> RunAsync(Func<string> argumentsFactory, Action<string> onLine)
    {
        string arguments;
        try
        {
            arguments = argumentsFactory();
        }
        catch (ArgumentException ex)
        {
            onLine($"❌ 命令构造被拒绝：{ex.Message}");
            return -1;
        }

        onLine($"$ netsh {arguments}");
        try
        {
            return await _runner.RunAsync("netsh", arguments, onLine, timeout: CommandTimeout).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            onLine($"❌ 命令构造被拒绝：{ex.Message}");
            return -1;
        }
    }

    private static void RequireIPv4(string text, string paramName)
    {
        // 【审查修复】IPAddress.TryParse 接受旧式简写（"1.2.3"→1.2.0.3、"1.2"、十六进制混合），
        // 会通过校验但 netsh 必然报错——改用严格四段十进制（IpValidation）
        if (!IpValidation.IsIPv4(text))
        {
            throw new ArgumentException($"“{text}” 不是有效的 IPv4 地址", paramName);
        }
    }
}
