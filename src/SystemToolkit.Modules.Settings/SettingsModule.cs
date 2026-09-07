using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;

namespace SystemToolkit.Modules.Settings;

/// <summary>
/// 设置 模块（2026-09-07 落地页面骨架，主人裁定：备份设置并入本模块统一管理）。
/// 依赖只向下取 Core 服务（BackupConfigService），不引用任何业务模块。
/// </summary>
public sealed class SettingsModule : ModuleBase
{
    public override string Id => "settings";

    public override string DisplayName => "设置";

    public override int Order => 10;

    public override bool CanDisable => false;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<SettingsView>();

    public override void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<ILogger>("settings", new FileLogger("settings"));
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsView>();
    }
}
