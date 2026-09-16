using Hardware.Info;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Network.Connections;
using SystemToolkit.Core.Overview.Services;

namespace SystemToolkit.Modules.Overview;

/// <summary>
/// 本机概览模块。
/// 复用说明：采集/格式化/报告构建全部来自旧工程 Core.Overview（直接复用）；
/// QuickPulseSampler 为新增（内存/磁盘 2s 采样，旧采样器无此数据源，理由见该类注释）。
/// </summary>
public sealed class OverviewModule : ModuleBase
{
    public override string Id => "overview";

    public override string DisplayName => "本机概览";

    public override int Order => 1;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<OverviewView>();

    public override void RegisterServices(IServiceCollection services)
    {
        // Hardware.Info 采集器（IHardwareInfo 缺注册会导致 OverviewService 解析失败 → 启动闪退）
        services.AddSingleton<IHardwareInfo, HardwareInfo>();
        // 可观测日志（NullLogger 吞异常教训）：落 %LOCALAPPDATA%\SystemToolkit\logs\overview-日期.log
        // 🔴 必须键控注册（审查 2026-09-04）：非键 ILogger 是单槽，会被后注册模块覆盖（概览日志曾全串进 appmanager 日志）
        services.AddKeyedSingleton<ILogger>("overview", new FileLogger("overview"));
        services.AddSingleton(sp => new HardwareSensorProbe(sp.GetRequiredKeyedService<ILogger>("overview")));
        services.AddSingleton(sp => new OverviewService(
            hw: sp.GetRequiredService<IHardwareInfo>(),
            sensorProbe: sp.GetRequiredService<HardwareSensorProbe>(),
            logger: sp.GetRequiredKeyedService<ILogger>("overview")));
        services.AddSingleton<LiveUsageSampler>();
        services.AddSingleton<QuickPulseSampler>();
        services.AddSingleton<TopProcessSampler>();
        // B7b：TCP 端点表（GetExtendedTcpTable，免提权；互操作由 CsWin32 生成）
        services.AddSingleton<TcpConnectionTable>();
        // 磁盘快照缓存（复用旧工程实现提取至 Core）：启动秒显，慢变量持久化
        services.AddSingleton(sp => new OverviewSnapshotCache(sp.GetRequiredKeyedService<ILogger>("overview")));
        services.AddSingleton(sp => new OverviewViewModel(
            sp.GetRequiredService<OverviewService>(),
            sp.GetRequiredService<LiveUsageSampler>(),
            sp.GetRequiredService<QuickPulseSampler>(),
            sp.GetRequiredService<OverviewSnapshotCache>(),
            sp.GetRequiredKeyedService<ILogger>("overview"),
            sp.GetRequiredService<TopProcessSampler>(),
            sp.GetRequiredService<TcpConnectionTable>()));
        // 🔴 视图必须 Transient：宿主在主题切换后经 CreateView 重建当前页，
        //    以重新解析 {StaticResource} 派生样式（Style.BasedOn 不支持 DynamicResource）。
        //    View 注册为单例时 CreateView 恒返回同一实例 → PageHost.Content 赋同一对象是 WPF 空操作
        //    → 视图停留在旧主题包的颜色上（2026-09-15 实机“浅色下白字压白底”事故）。
        //    视图是无状态壳（DataContext 由 VM 提供），重建只重置 UI 局部状态。
        services.AddTransient<OverviewView>();
    }
}
