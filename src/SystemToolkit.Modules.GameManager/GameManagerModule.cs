using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.GameManager.Services;

namespace SystemToolkit.Modules.GameManager;

/// <summary>
/// 游戏管理 模块（扩展模块，可在设置中禁用）。
/// Steam 本地库只读解析 + 启动/商店/目录/卸载引导；全部数据来自本地 VDF/ACF，不调用需登录态的 Web API。
/// 数据加载仅由页面 Loaded 触发——无定时器/文件监听，模块禁用后零残留（设计 §1.1）。
/// </summary>
public sealed class GameManagerModule : ModuleBase
{
    public override string Id => "gamemanager";

    public override string DisplayName => "游戏管理";

    public override int Order => 8;

    public override bool CanDisable => true;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<GameManagerView>();

    public override void RegisterServices(IServiceCollection services)
    {
        // 可观测日志（键控注册，审查 2026-09-04 纪律）
        services.AddKeyedSingleton<ILogger>("gamemanager", new FileLogger("gamemanager"));
        // Steam 全栈服务（Core/Game 域，旧工程 1:1 移植）
        services.AddSingleton(sp => new SteamService(sp.GetRequiredKeyedService<ILogger>("gamemanager")));
        // 工厂注册：接通键控日志器（原 ILogger? 可选参数实际拿 NullLogger）+ UI Dispatcher（审查 🔴-5）
        services.AddSingleton(sp => new GameManagerViewModel(
            sp.GetRequiredService<SteamService>(),
            sp.GetRequiredKeyedService<ILogger>("gamemanager"),
            System.Windows.Application.Current?.Dispatcher));
        services.AddSingleton<GameManagerView>();
    }
}
