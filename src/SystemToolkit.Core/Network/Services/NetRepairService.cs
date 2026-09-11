using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Network.Models;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// <see cref="INetRepairService"/> 实现：命令构造收口在 <see cref="NetshArgs"/>（执行与展示共用）；
/// renew / bounce 的适配器自动选取收在本服务（依赖 <see cref="INetworkInfoService"/>），
/// ViewModel 不必持有适配器状态。命令先回显（<c>$ ipconfig …</c> / <c>$ netsh …</c>）再执行。
/// </summary>
public sealed class NetRepairService : INetRepairService
{
    /// <summary>bounce（重启网卡）在 disable 与 enable 之间的间隔：给驱动留出完成停用的时间。</summary>
    private static readonly TimeSpan BounceGap = TimeSpan.FromMilliseconds(1500);

    private readonly ICommandRunner _runner;
    private readonly INetworkInfoService _info;
    private readonly ILogger _logger;

    /// <summary>构造；命令执行器与适配器信息经接口注入（适配器自动选取依赖后者）；日志可选（LOG-3）。</summary>
    public NetRepairService(ICommandRunner runner, INetworkInfoService info, ILogger? logger = null)
    {
        _runner = runner;
        _info = info;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc cref="INetRepairService.Steps"/>
    public IReadOnlyList<RepairStepDescriptor> Steps { get; } = new[]
    {
        new RepairStepDescriptor(
            "flushdns", "刷新 DNS 缓存",
            "清除本机 DNS 解析缓存。改完 DNS 或遇到「网址打不开但 IP 能通」时先试这个。",
            RequiresAdmin: false, RequiresReboot: false, CommandPreview: "ipconfig /flushdns"),
        new RepairStepDescriptor(
            "renew", "重新获取 IP",
            "向 DHCP 服务器重新申请 IP 地址（自动选用首选已连接适配器）。IP 冲突 / 拿到错误网段时使用。",
            RequiresAdmin: false, RequiresReboot: false, CommandPreview: "ipconfig /renew \"<已连接网卡>\""),
        new RepairStepDescriptor(
            "bounce", "重启网卡（软重连）",
            "对网卡执行一次禁用再启用。WiFi 掉线、IP 状态异常时的标准动作；执行期间该网卡会短暂断网。",
            RequiresAdmin: true, RequiresReboot: false, CommandPreview: "netsh interface set interface \"<网卡>\" admin=disable → enable"),
        new RepairStepDescriptor(
            "arpclear", "清除 ARP 缓存",
            "删除本机 ARP 表全部条目（系统会自动重建）。局域网内其它设备换了网卡/IP 导致「Ping 网关不通但网线没问题」时使用。",
            RequiresAdmin: true, RequiresReboot: false, CommandPreview: "arp -d *"),
        new RepairStepDescriptor(
            "winsockreset", "重置 Winsock",
            "清空 Winsock 目录并还原网络组件默认配置。可修复软件卸载残留导致的奇怪网络故障；执行后必须重启计算机。",
            RequiresAdmin: true, RequiresReboot: true, CommandPreview: "netsh winsock reset"),
        new RepairStepDescriptor(
            "ipreset", "重置 TCP/IP",
            "重置 TCP/IP 协议栈（注册表级）。最后手段：之前的修复都无效时使用；执行后必须重启计算机。",
            RequiresAdmin: true, RequiresReboot: true, CommandPreview: "netsh int ip reset"),
    };

    /// <inheritdoc cref="INetRepairService.ExecuteAsync"/>
    public async Task<int> ExecuteAsync(string stepId, Action<string> onLine, string? adapter = null, CancellationToken ct = default)
    {
        LogTiming timing = _logger.Time("RepairStep");
        try
        {
            int exit = await ExecuteStepAsync(stepId, onLine, adapter, ct).ConfigureAwait(false);
            timing.Complete(exit == 0 ? LogResult.Success : LogResult.Failed,
                exit == 0 ? LogLevel.Info : LogLevel.Warn,
                $"修复步骤「{stepId}」结束（退出码 {exit}）");
            return exit;
        }
        catch (ArgumentException ex)
        {
            timing.Complete(LogResult.Rejected, LogLevel.Warn, $"未知修复步骤：{ex.Message}");
            throw;
        }
    }

    private async Task<int> ExecuteStepAsync(string stepId, Action<string> onLine, string? adapter, CancellationToken ct)
    {
        switch (stepId)
        {
            case "flushdns":
                return await RunIpconfigAsync(NetshArgs.FlushDns, onLine, ct).ConfigureAwait(false);

            case "renew":
                {
                    string? name = await ResolveAdapterOrNullAsync(adapter, onLine).ConfigureAwait(false);
                    if (name is null)
                    {
                        return -1; // 没有已连接适配器：不启动进程（设计文档 §5.2 降级路径）
                    }

                    // DHCP 服务器无响应时 renew 可阻塞 30s+：给 120s 宽限（【审查修复】2.2）
                    return await RunIpconfigAsync(NetshArgs.Renew(name), onLine, ct, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
                }

            case "bounce":
                {
                    string? name = await ResolveAdapterOrNullAsync(adapter, onLine).ConfigureAwait(false);
                    if (name is null)
                    {
                        return -1;
                    }

                    onLine($"$ netsh {NetshArgs.SetAdapterEnabled(name, enabled: false)}");
                    int disableExit = await _runner.RunAsync("netsh", NetshArgs.SetAdapterEnabled(name, enabled: false), onLine, ct, CommandTimeout).ConfigureAwait(false);
                    if (disableExit != 0)
                    {
                        onLine($"⚠️ 禁用阶段失败（退出码 {disableExit}），不执行重新启用");
                        return disableExit;
                    }

                    await Task.Delay(BounceGap, ct).ConfigureAwait(false);
                    onLine($"$ netsh {NetshArgs.SetAdapterEnabled(name, enabled: true)}");
                    int enableExit = await _runner.RunAsync("netsh", NetshArgs.SetAdapterEnabled(name, enabled: true), onLine, ct, CommandTimeout).ConfigureAwait(false);
                    if (enableExit != 0)
                    {
                        // 【核实报告 N2】disable 已成功而 enable 失败 = 网卡停留禁用态，比 bounce 目标更糟——自动重试一次
                        onLine("[修复] ⚠️ 重新启用失败，自动重试一次");
                        await Task.Delay(BounceGap, ct).ConfigureAwait(false);
                        onLine($"$ netsh {NetshArgs.SetAdapterEnabled(name, enabled: true)}");
                        enableExit = await _runner.RunAsync("netsh", NetshArgs.SetAdapterEnabled(name, enabled: true), onLine, ct, CommandTimeout).ConfigureAwait(false);
                        if (enableExit != 0)
                        {
                            onLine($"[修复] ❌ 重试仍失败：网卡「{name}」当前处于禁用状态，请到设备管理器或系统网络设置手动启用");
                        }
                    }

                    return enableExit;
                }

            case "winsockreset":
                return await RunNetshAsync(NetshArgs.WinsockReset, onLine, ct).ConfigureAwait(false);

            case "arpclear":
                onLine("$ arp -d *");
                return await _runner.RunAsync("arp", "-d *", onLine, ct, CommandTimeout).ConfigureAwait(false);

            case "ipreset":
                return await RunNetshAsync(NetshArgs.IpReset, onLine, ct).ConfigureAwait(false);

            default:
                // 调用方（VM）按目录 Id 触发，传错是编程错误而非运行时状况
                throw new ArgumentException($"未知的修复步骤 Id：{stepId}", nameof(stepId));
        }
    }

    /// <inheritdoc cref="INetRepairService.RunSafeSequenceAsync"/>
    public async Task<IReadOnlyList<string>> RunSafeSequenceAsync(Action<string> onLine, CancellationToken ct = default)
    {
        LogTiming timing = _logger.Time("RepairSafeSequence");
        var executed = new List<string>(2);

        int flushExit = await ExecuteAsync("flushdns", onLine, ct: ct).ConfigureAwait(false);
        if (flushExit != 0)
        {
            onLine($"[修复] ❌ 安全序列在「刷新 DNS 缓存」处失败（退出码 {flushExit}），已停止——未执行后续步骤");
            timing.Complete(LogResult.Failed, LogLevel.Warn, $"安全序列中止于 flushdns（已执行 {executed.Count} 步）");
            return executed;
        }

        executed.Add("flushdns");

        int renewExit = await ExecuteAsync("renew", onLine, ct: ct).ConfigureAwait(false);
        if (renewExit != 0)
        {
            onLine($"[修复] ❌ 安全序列在「重新获取 IP」处失败（退出码 {renewExit}），已停止");
            timing.Complete(LogResult.Failed, LogLevel.Warn, $"安全序列中止于 renew（已执行 {executed.Count} 步）");
            return executed;
        }

        executed.Add("renew");
        timing.Complete(LogResult.Success, LogLevel.Info, "安全序列完成（flushdns + renew）");
        return executed;
    }

    /// <summary>
    /// 解析目标适配器：显式指定优先；否则自动选首选已连接适配器并<b>在日志写明</b>（可观测）；
    /// 没有已连接适配器时返回 null（由调用方转成退出码 -1，不启动进程）。
    /// </summary>
    private async Task<string?> ResolveAdapterOrNullAsync(string? adapter, Action<string> onLine)
    {
        if (!string.IsNullOrWhiteSpace(adapter))
        {
            return adapter;
        }

        IReadOnlyList<NetAdapterInfo> adapters = await _info.GetAdaptersAsync().ConfigureAwait(false);
        // 【审查修复】优先物理网卡（以太网 / 无线）：VPN / 虚拟化（VirtualBox、Hyper-V、WSL）
        // 虚拟网卡常呈 Up 且枚举靠前，renew 对无 DHCP 租约的虚拟网卡无意义、bounce 会瞬断其会话
        NetAdapterInfo? up = adapters.FirstOrDefault(a =>
            a.Status == OperStatus.Up && a.Type is NetType.Ethernet or NetType.Wireless)
            ?? adapters.FirstOrDefault(a => a.Status == OperStatus.Up);
        if (up is null)
        {
            onLine("❌ 该操作需要网卡，但当前没有已连接的适配器");
            return null;
        }

        onLine($"自动选用首选已连接适配器：{up.Name}");
        return up.Name;
    }

    // 【审查修复】2.2：命令超时——外部进程可能无限挂起，不设超时会锁死 UI 防重入门闩
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private async Task<int> RunIpconfigAsync(string arguments, Action<string> onLine, CancellationToken ct, TimeSpan? timeout = null)
    {
        onLine($"$ ipconfig {arguments}");
        return await _runner.RunAsync("ipconfig", arguments, onLine, ct, timeout ?? CommandTimeout).ConfigureAwait(false);
    }

    private async Task<int> RunNetshAsync(string arguments, Action<string> onLine, CancellationToken ct)
    {
        onLine($"$ netsh {arguments}");
        return await _runner.RunAsync("netsh", arguments, onLine, ct, CommandTimeout).ConfigureAwait(false);
    }
}
