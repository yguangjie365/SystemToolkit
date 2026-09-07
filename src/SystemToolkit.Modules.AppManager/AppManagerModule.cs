using Hardware.Info;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Overview.Services;
using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Modules.AppManager;

/// <summary>
/// 软件管理 模块。
/// 复用说明：WingetService/EnvListService/EnvCatalog 等来自旧工程 Core.Environment（已搬移）；
/// VM 编排逻辑改编自旧工程 EnvManagerViewModel（1221 行，改造点见该类注释）。
/// </summary>
public sealed class AppManagerModule : ModuleBase
{
    public override string Id => "appmanager";

    public override string DisplayName => "软件管理";

    public override int Order => 2;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<AppManagerView>();

    public override void RegisterServices(IServiceCollection services)
    {
        // Hardware.Info 与 Overview 共用同一实例（DI 单例已由 OverviewModule 注册，这里防御性补充）
        services.TryAddSingleton<IHardwareInfo, HardwareInfo>();
        services.TryAddSingleton<HardwareSensorProbe>();

        services.AddSingleton<EnvListService>();
        services.AddSingleton<IWingetClient, WingetService>();
        // 可观测日志（NullLogger 吞异常教训）：落 %LOCALAPPDATA%\SystemToolkit\logs\appmanager-日期.log
        // 🔴 必须键控注册（审查 2026-09-04）：非键 ILogger 是单槽，会覆盖 OverviewModule 的注册
        services.AddKeyedSingleton<ILogger>("appmanager", new FileLogger("appmanager"));
        services.AddSingleton(sp => new AppManagerViewModel(
            sp.GetRequiredService<EnvListService>(),
            sp.GetRequiredService<IWingetClient>(),
            sp.GetRequiredKeyedService<ILogger>("appmanager")));
        services.AddSingleton(sp => new AppManagerView(
            sp.GetRequiredService<AppManagerViewModel>(),
            sp.GetRequiredKeyedService<ILogger>("appmanager")));
    }
}

