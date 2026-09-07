using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Modules.NetManager;

/// <summary>
/// 网络管理 模块（核心模块，不可禁用）。
/// 设置（网卡/IP/DNS/代理）+ 诊断（诊断链/定向排查）+ 修复 + 优化（TCP 调优/跃点数）+
/// 全量配置快照回滚；所有修改类操作走「快照 → 修改 → 验证 → 回滚」纪律（设计 04 §5）。
/// <para>
/// 提权模型：netsh/ipconfig/arp 写命令经 <see cref="ElevatingCommandRunner"/> 白名单装饰器
/// 按需 UAC（用户拒绝 → 退出码 1223 安全终止，无副作用）；HKLM 节流注册表经 Helper
/// throttling 窄动词。读命令一律直连不触发 UAC。
/// </para>
/// </summary>
public sealed class NetManagerModule : ModuleBase
{
    public override string Id => "netmanager";

    public override string DisplayName => "网络管理";

    public override int Order => 6;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<NetManagerView>();

    public override void RegisterServices(IServiceCollection services)
    {
        // 可观测日志（键控注册，审查 2026-09-04 纪律）
        services.AddKeyedSingleton<ILogger>("netmanager", new FileLogger("netmanager"));

        // 命令执行链：裸 CommandRunner（直连 + 编码择优）→ 提权装饰器作为 ICommandRunner 注入
        services.AddSingleton<CommandRunner>();
        services.AddSingleton<ElevatingCommandRunner>(sp => new ElevatingCommandRunner(
            sp.GetRequiredService<CommandRunner>()));
        services.AddSingleton<ICommandRunner>(sp => sp.GetRequiredService<ElevatingCommandRunner>());

        // 探测与信息（系统边界实现）
        services.AddSingleton<IElevationProvider, WindowsElevationProvider>();
        services.AddSingleton<INetworkInfoService, NetworkInfoService>();
        services.AddSingleton<INetProbe, WindowsNetProbe>();
        services.AddSingleton<IHostsCheckService, HostsCheckService>();

        // 业务服务（老工程 1:1 移植 + V0.4 新增）
        services.AddSingleton<INetConfigService, NetConfigService>();
        services.AddSingleton<INetDiagnosticService, NetDiagnosticService>();
        services.AddSingleton<INetRepairService, NetRepairService>();
        services.AddSingleton<ITcpTuningService>(sp => new TcpTuningService(
            sp.GetRequiredService<ICommandRunner>(),
            snapshotPath: null,
            throttlingWriter: (value, log) =>
                sp.GetRequiredService<ElevatingCommandRunner>().RunThrottlingWriteAsync(value, log)));
        services.AddSingleton<DnsProbeService>();
        services.AddSingleton<ContinuousPingService>();
        services.AddSingleton<INetworkSnapshotService, NetworkSnapshotService>();

        // VM 组合根 + 视图
        services.AddSingleton<NetManagerViewModel>();
        services.AddSingleton<NetManagerView>();
    }
}
